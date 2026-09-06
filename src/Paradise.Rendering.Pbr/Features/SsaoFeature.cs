using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Screen-space ambient occlusion's input: a world-position pre-pass at
/// <see cref="RenderPassEvent.Prepass"/> over the opaque bucket, into an Rgba32Float target the
/// scene's group 3 samples via textureLoad.
///
/// <para>The pass is declared every frame and runs only when something reads it: in frames the
/// effect is on, the position target is published as <see cref="PbrResults.SsaoPosition"/> and
/// the scene binds it; otherwise the scene binds black and the graph culls the pre-pass. The
/// uniforms carry intensity 0 in exactly those frames, so the shader never samples an unwritten
/// target.</para></summary>
public sealed class SsaoFeature : IRenderFeature
{
    private readonly PbrContext _ctx;
    private readonly ShaderProgramDesc _program;
    private readonly PipelineHandle _pipeline;
    private PipelineHandle _skinnedPipeline;
    private readonly BindGroupHandle _jointGroup;

    internal SsaoFeature(PbrContext ctx)
    {
        _ctx = ctx;
        var renderer = ctx.Renderer;

        // Reuses the main draw ring/group (its group 0 is the same DrawUniforms, made
        // dynamic-offset). Vertex layout is position-only over the mesh stride.
        _program = ShaderPrograms.WithDynamicDrawRing(ShaderPrograms.Load("Shaders.positionPrepass"));
        _pipeline = renderer.CreatePipeline(
            _program, TextureFormat.Rgba32Float,
            depthStencilFormat: TextureFormat.Depth32Float,
            depthWriteEnabled: true,
            depthCompare: CompareFunction.Less);
        _jointGroup = renderer.CreateBindGroup(new BindGroupDesc("PbrPrepassJointGroup", ShaderPrograms.FindGroup(_program, 1), new[]
        {
            BindGroupEntryDesc.ForBuffer(0, ctx.JointBuffer, 0, ctx.JointBufferBytes),
        }));
        UniformBuffer = renderer.CreateBuffer(new BufferDesc(
            "PbrSsaoUniforms", (ulong)Unsafe.SizeOf<SsaoUniformsGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst));

        EnsureTargets();
    }

    public string Name => "Ssao";
    public bool Enabled => true;
    public FrameRequirements Requires => FrameRequirements.None;

    /// <summary>Group-3 uniforms: intensity, radius, bias, power, and the screen size.</summary>
    internal BufferHandle UniformBuffer { get; }

    public void Resize(uint width, uint height) => EnsureTargets();

    private void EnsureTargets()
    {
        _ctx.Targets.Ensure(PbrTargets.SsaoPosition, _ctx.FrameTarget(TextureFormat.Rgba32Float));
        _ctx.Targets.Ensure(PbrTargets.SsaoPrepassDepth, _ctx.FrameTarget(TextureFormat.Depth32Float));
    }

    public void Setup(in FrameContext frame)
    {
        var scene = _ctx.Scene;
        var s = scene.Ssao;
        // Intensity 0 (SSAO off, or nothing opaque this frame) makes the shader skip position
        // sampling — the same condition that decides whether the target is published below.
        var runs = s.Enabled && _ctx.Opaque.Count > 0;
        var uniforms = new SsaoUniformsGpu
        {
            Params = new Vector4(runs ? s.Intensity : 0f, MathF.Max(s.Radius, 1e-3f), s.Bias, MathF.Max(s.Power, 1e-3f)),
            Screen = new Vector4(1f / _ctx.Width, 1f / _ctx.Height, _ctx.Width, _ctx.Height),
        };
        _ctx.Renderer.UpdateBuffer<SsaoUniformsGpu>(UniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));

        var position = frame.Graph.Texture(PbrTargets.SsaoPosition);
        frame.Graph.AddRasterPass("Ssao.Position", RenderPassEvent.Prepass)
            .Color(0, position, LoadOp.Clear, clear: new ColorRgba(0f, 0f, 0f, 0f))
            .Depth(frame.Graph.Texture(PbrTargets.SsaoPrepassDepth), LoadOp.Clear, clear: 1f)
            .Record(this, RecordPrepass);

        if (runs) frame.Blackboard.Publish(PbrResults.SsaoPosition, position);
    }

    // opaque[i] uses the same dynamic offset EncodeBucket fills for it, so no extra ring space or
    // upload is needed — which is also why this recorder assumes the opaque bucket starts at slot 0.
    private static void RecordPrepass(SsaoFeature self, ref PassRecording pass, int _)
    {
        ref var encoder = ref pass.Encoder;
        var ctx = self._ctx;
        encoder.SetBindGroup(1, self._jointGroup);
        var skinnedActive = (bool?)null;
        for (var i = 0; i < ctx.Opaque.Count; i++)
        {
            var primitive = ctx.Opaque[i].Primitive;
            var skinned = primitive.Skinned && ctx.Opaque[i].Instance.JointOffset >= 0;
            if (skinnedActive != skinned)
            {
                encoder.SetPipeline(skinned ? self.SkinnedPipeline() : self._pipeline);
                skinnedActive = skinned;
            }
            encoder.SetBindGroup(0, ctx.DrawGroup, dynamicOffset: (uint)(i * ctx.DrawStride));
            encoder.SetVertexBuffer(0, primitive.VertexBuffer, 0, primitive.VertexByteLength);
            encoder.SetIndexBuffer(primitive.IndexBuffer, IndexFormat.Uint32, 0, primitive.IndexByteLength);
            encoder.DrawIndexed(new DrawIndexedCommand(primitive.IndexCount, 1, 0, 0, 0));
        }
    }

    private PipelineHandle SkinnedPipeline()
    {
        if (_skinnedPipeline.IsValid) return _skinnedPipeline;
        _skinnedPipeline = _ctx.Renderer.CreatePipeline(
            _program, TextureFormat.Rgba32Float,
            depthStencilFormat: TextureFormat.Depth32Float,
            depthWriteEnabled: true,
            depthCompare: CompareFunction.Less,
            vertexEntryPoint: "vertexMainSkinned");
        return _skinnedPipeline;
    }

    public void Dispose()
    {
        var renderer = _ctx.Renderer;
        if (_skinnedPipeline.IsValid) renderer.DestroyPipeline(_skinnedPipeline);
        renderer.DestroyPipeline(_pipeline);
        renderer.DestroyBindGroup(_jointGroup);
        renderer.DestroyBuffer(UniformBuffer);
    }
}
