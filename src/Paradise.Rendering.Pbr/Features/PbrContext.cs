using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Holds renderer resources and frame data shared by PBR features.</summary>
/// <remarks>Feature outputs travel through the blackboard or explicit constructor dependencies,
/// keeping the context free of feature-to-feature access.</remarks>
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

        DrawStride = renderer.UniformBufferOffsetAlignment;
        _drawRing = new DrawRing(renderer, "PbrDrawRing", programs.Group(0), (uint)Unsafe.SizeOf<DrawUniformsGpu>());
        _drawRing.EnsureCapacity(DrawBufferCapacity.Initial);

        // Joint palettes are resolution independent, so this is allocated once and never resized.
        JointCapacity = PbrRenderer.MaxSkinnedJoints;
        JointPalettes = new Matrix4x4[JointCapacity];
        for (var i = 0; i < JointCapacity; i++) JointPalettes[i] = Matrix4x4.Identity;
        JointBuffer = renderer.CreateBuffer(new BufferDesc(
            "PbrJointPalettes", JointBufferBytes, BufferUsage.Storage | BufferUsage.CopyDst));
        renderer.UpdateBuffer<Matrix4x4>(JointBuffer, 0, JointPalettes);

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
    public MaterialResourceCache Materials { get; set; } = null!;

    public uint Width { get; private set; }
    public uint Height { get; private set; }

    /// <summary>The per-draw uniform ring shared by the main pass and the SSAO pre-pass, which
    /// re-reads the slot the main pass filled for the same instance.</summary>
    public uint DrawStride { get; }
    public BufferHandle DrawUniformRing => _drawRing.Buffer;
    public BindGroupHandle DrawGroup => _drawRing.Group;
    public byte[] DrawStaging => _drawRing.Staging;
    public int DrawCapacity => _drawRing.Capacity;

    public void EnsureDrawCapacity(int required) => _drawRing.EnsureCapacity(required);

    /// <summary>Next free draw-ring slot this frame. The blend bucket continues where the opaque
    /// bucket stopped: the ring does not care which pass consumes a slot, only that no two draws
    /// claim the same one.</summary>
    public int DrawIndex;

    public List<(PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth)> Opaque { get; } = [];
    public List<(PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth)> Blend { get; } = [];

    /// <summary>Joint palettes for skinned instances, packed end to end and indexed by
    /// <see cref="PbrInstance.JointOffset"/>. Bound unconditionally wherever a program declares
    /// it: WebGPU requires every declared binding to be present, so a scene with no skinned mesh
    /// still binds this at its minimum size.</summary>
    public BufferHandle JointBuffer { get; }
    public Matrix4x4[] JointPalettes { get; }
    public int JointCapacity { get; }
    public ulong JointBufferBytes => (ulong)(JointCapacity * Unsafe.SizeOf<Matrix4x4>());
    public int JointHighWater;

    /// <summary>Linear, clamped: what every fullscreen pass samples with.</summary>
    public SamplerHandle LinearClampSampler { get; }

    /// <summary>The scene as the compute tracer sees it: merged hierarchies and the frame's
    /// instances. Built by the renderer before features set up, in frames something traces.</summary>
    public TraceScene Trace { get; }

    /// <summary>The frame uniforms (lights, shadow matrices, ambient, camera). Filled by the scene
    /// feature each frame; bound by it and by the compute passes that shade ray hits.</summary>
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
        View = scene.Camera.View;
        _previousProjectionJitterUv = ProjectionJitterUv;
        SetProjection(scene.Camera.Projection);
        DrawIndex = 0;
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
