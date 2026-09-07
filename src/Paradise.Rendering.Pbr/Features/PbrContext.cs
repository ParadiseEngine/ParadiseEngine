using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>What the PBR features share: the backend, the graph's collaborators, the draw ring
/// every geometry pass fills, the joint palettes, and the frame's scene once
/// <see cref="PbrRenderer.RenderFrame"/> has partitioned it.
///
/// <para>A feature reaches nothing of another feature through here. What one feature produces
/// for another travels by name on the blackboard or, for the two engine features that are
/// genuinely one thing split in two (scene lighting reads the shadow plan), as a constructor
/// argument the renderer supplies.</para></summary>
internal sealed class PbrContext : IDisposable
{
    public const int MaxDrawsPerFrame = 4096;

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
        DrawStaging = new byte[DrawStride * MaxDrawsPerFrame];
        var ringDesc = new BufferDesc("PbrDrawRing", (ulong)DrawStride * MaxDrawsPerFrame, BufferUsage.Uniform | BufferUsage.CopyDst);
        DrawUniformRing = renderer.CreateBuffer(in ringDesc);
        var drawGroupDesc = new BindGroupDesc("PbrDrawGroup", programs.Group(0), new[]
        {
            BindGroupEntryDesc.ForBuffer(0, DrawUniformRing, 0, (ulong)Unsafe.SizeOf<DrawUniformsGpu>()),
        });
        DrawGroup = renderer.CreateBindGroup(in drawGroupDesc);

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
    public BufferHandle DrawUniformRing { get; }
    public BindGroupHandle DrawGroup { get; }
    public byte[] DrawStaging { get; }

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
    public Matrix4x4 ViewProjection { get; private set; }

    public void BeginFrame(PbrScene scene, in Matrix4x4 view, in Matrix4x4 viewProjection)
    {
        Scene = scene;
        View = view;
        ViewProjection = viewProjection;
        DrawIndex = 0;
    }

    public void Resize(uint width, uint height)
    {
        Width = width;
        Height = height;
    }

    public TextureDesc FrameTarget(TextureFormat format) => PbrTargets.RenderTarget(Width, Height, format);

    public void Dispose()
    {
        Renderer.DestroyBindGroup(DrawGroup);
        Renderer.DestroyBuffer(DrawUniformRing);
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
