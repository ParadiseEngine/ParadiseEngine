using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Holds renderer resources and frame data shared by PBR features.</summary>
/// <remarks>Features publish frame results through the blackboard; this context owns common resources.</remarks>
internal sealed class PbrContext : IDisposable
{
    private readonly DrawRing _drawRing;

    public PbrContext(IRenderer renderer, ILogger log, MaterialPrograms programs, uint width, uint height)
    {
        Renderer = renderer;
        Log = log;
        Programs = programs;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Targets = new GraphTextureRegistry(renderer);
        BindGroups = new BindGroupCache(renderer);
        Fallbacks = new PbrFallbackResources(renderer, Targets);

        DrawStride = renderer.UniformBufferOffsetAlignment;
        _drawRing = new DrawRing(renderer, "PbrDrawRing", programs.Group(0), (uint)Unsafe.SizeOf<DrawUniformsGpu>());
        _drawRing.EnsureCapacity(DrawBufferCapacity.Initial);

        // Joint palettes start small and grow (EnsureJointCapacity) as skinned instances need them.
        JointCapacity = PbrRenderer.InitialSkinnedJoints;
        JointPalettes = new Matrix4x4[JointCapacity];
        JointPalettes.AsSpan().Fill(Matrix4x4.Identity);
        JointBuffer = CreateJointBuffer();

        Trace = new TraceScene(renderer, log);
        FrameUniformBuffer = renderer.CreateBuffer(new BufferDesc(
            "PbrFrameUniforms", (ulong)Unsafe.SizeOf<FrameUniformsGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst));

        LinearClampSampler = renderer.CreateSampler(new SamplerDesc(
            "PbrCompositeSampler",
            SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge,
            SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Nearest));
    }

    public IRenderer Renderer { get; }
    public ILogger Log { get; }
    public MaterialPrograms Programs { get; }
    public GraphTextureRegistry Targets { get; }
    public BindGroupCache BindGroups { get; }
    public PbrFallbackResources Fallbacks { get; }
    public MaterialResourceCache Materials { get; set; } = null!;
    public float SpecularAaVariance { get; set; }
    public float SpecularAaClamp { get; set; }
    public bool TraceGlobalIllumination { get; set; }

    public uint Width { get; private set; }
    public uint Height { get; private set; }

    /// <summary>Byte stride of the per-draw uniform ring in the final frame draw order.</summary>
    public uint DrawStride { get; }
    public BufferHandle DrawUniformRing => _drawRing.Buffer;
    public BindGroupHandle DrawGroup => _drawRing.Group;
    public byte[] DrawStaging => _drawRing.Staging;
    public int DrawCapacity => _drawRing.Capacity;

    public void EnsureDrawCapacity(int required) => _drawRing.EnsureCapacity(required);

    public PbrFrameData Frame { get; } = new();
    public List<FrameDraw> Opaque => Frame.Opaque;
    public List<FrameDraw> Blend => Frame.Blend;

    /// <summary>Joint palettes for skinned instances, packed end to end and indexed by
    /// <see cref="PbrInstance.JointOffset"/>. Bound unconditionally wherever a program declares
    /// it: WebGPU requires every declared binding to be present, so a scene with no skinned mesh
    /// still binds this at its minimum size.</summary>
    public BufferHandle JointBuffer { get; private set; }
    public Matrix4x4[] JointPalettes { get; private set; }
    public int JointCapacity { get; private set; }
    public ulong JointBufferBytes => (ulong)JointCapacity * (ulong)Unsafe.SizeOf<Matrix4x4>();
    public int JointHighWater;

    /// <summary>Bumped whenever <see cref="JointBuffer"/> is replaced. A bind group built over the
    /// palette outside the frame graph rebuilds when this changes (<see cref="JointBinding"/>).</summary>
    public int JointGeneration { get; private set; }

    /// <summary>Grows the palette to hold at least <paramref name="required"/> matrices, doubling up to
    /// <see cref="PbrRenderer.MaxSkinnedJoints"/>. Staged matrices are kept; new slots are identity.
    /// Call between frames: bind groups recorded this frame name the buffer being replaced.</summary>
    /// <returns>False when <paramref name="required"/> is beyond the maximum.</returns>
    public bool EnsureJointCapacity(int required)
    {
        if (required <= JointCapacity) return true;
        if (required > PbrRenderer.MaxSkinnedJoints) return false;
        var capacity = JointCapacity;
        while (capacity < required) capacity = (int)Math.Min((long)capacity * 2, PbrRenderer.MaxSkinnedJoints);

        var palettes = new Matrix4x4[capacity];
        JointPalettes.CopyTo(palettes, 0);
        palettes.AsSpan(JointCapacity).Fill(Matrix4x4.Identity);
        Renderer.DestroyBuffer(JointBuffer);
        JointPalettes = palettes;
        JointCapacity = capacity;
        JointBuffer = CreateJointBuffer();
        JointGeneration++;
        return true;
    }

    private BufferHandle CreateJointBuffer()
    {
        var buffer = Renderer.CreateBuffer(new BufferDesc(
            "PbrJointPalettes", JointBufferBytes, BufferUsage.Storage | BufferUsage.CopyDst));
        Renderer.UpdateBuffer<Matrix4x4>(buffer, 0, JointPalettes);
        return buffer;
    }

    /// <summary>Shared linear-clamp sampler for filtered fullscreen inputs.</summary>
    public SamplerHandle LinearClampSampler { get; }

    /// <summary>The scene as the compute tracer sees it: merged hierarchies and the frame's
    /// instances. Built by the renderer before features set up, in frames something traces.</summary>
    public TraceScene Trace { get; }

    /// <summary>The camera and lighting uniforms uploaded by frame lighting and shared with raster and compute passes.</summary>
    public BufferHandle FrameUniformBuffer { get; }
    public static ulong FrameUniformBytes => (ulong)Unsafe.SizeOf<FrameUniformsGpu>();

    // Frame-local: set by RenderFrame before any feature runs, read by every recorder.
    public PbrScene Scene { get; private set; } = null!;
    public Matrix4x4 View { get; private set; }
    public Matrix4x4 Projection { get; private set; }
    private Vector2 _previousProjectionJitterUv;
    public Vector2 ProjectionJitterUv { get; private set; }
    public Vector2 MotionJitterDelta => ProjectionJitterUv - _previousProjectionJitterUv;
    public Matrix4x4 ViewProjection { get; private set; }

    public void BeginFrame(PbrScene scene)
    {
        Scene = scene;
        TraceGlobalIllumination = false;
        Frame.DrawStatistics.Reset();
        View = scene.Camera.View;
        _previousProjectionJitterUv = ProjectionJitterUv;
        SetProjection(scene.Camera.Projection);
    }

    /// <summary>Sets the frame's projection without changing the authored camera.</summary>
    public void SetProjection(in Matrix4x4 projection, Vector2 jitterUv = default)
    {
        Projection = projection;
        ProjectionJitterUv = jitterUv;
        ViewProjection = PbrMath.ViewProjection(View, Projection);
    }

    public void Resize(uint width, uint height)
    {
        Width = width;
        Height = height;
    }

    public TextureDesc FrameTarget(TextureFormat format) => PbrTargets.RenderTarget(Width, Height, format);

    public void Dispose()
    {
        _drawRing.Dispose();
        Renderer.DestroyBuffer(JointBuffer);
        Renderer.DestroySampler(LinearClampSampler);
        Renderer.DestroyBuffer(FrameUniformBuffer);
        Trace.Dispose();
        BindGroups.Dispose();
        Fallbacks.Dispose();
        Targets.Dispose();
    }
}

/// <summary>What every post pass is: one fullscreen triangle with the pass's declared group 0.</summary>
internal static class Fullscreen
{
    public static void Record(ref PassRecording pass, PipelineHandle pipeline)
    {
        pass.Encoder.SetPipeline(pipeline);
        pass.SetBindGroup(0);
        pass.Encoder.Draw(new DrawCommand(3, 1, 0, 0));
    }
}

/// <summary>A one-entry bind group over the joint palette (binding 0) that follows the palette when
/// <see cref="PbrContext.EnsureJointCapacity"/> replaces it.</summary>
/// <remarks>For bind groups a feature holds across frames; frame-graph bindings resolve the current
/// buffer each frame on their own.</remarks>
internal sealed class JointBinding(PbrContext ctx, string name, BindGroupLayoutDesc layout) : IDisposable
{
    private BindGroupHandle _group;
    private int _generation = -1;

    /// <summary>The group over the current palette, rebuilt if the palette grew since it was made.</summary>
    public BindGroupHandle Group
    {
        get
        {
            if (_generation == ctx.JointGeneration) return _group;
            if (_group.IsValid) ctx.Renderer.DestroyBindGroup(_group);
            _group = ctx.Renderer.CreateBindGroup(new BindGroupDesc(name, layout, new[]
            {
                BindGroupEntryDesc.ForBuffer(0, ctx.JointBuffer, 0, ctx.JointBufferBytes),
            }));
            _generation = ctx.JointGeneration;
            return _group;
        }
    }

    public void Dispose()
    {
        if (_group.IsValid) ctx.Renderer.DestroyBindGroup(_group);
        _group = default;
    }
}
