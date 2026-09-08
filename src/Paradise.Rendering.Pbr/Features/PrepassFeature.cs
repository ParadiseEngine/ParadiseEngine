using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Produces opaque depth, world normals and SSAO uniforms for the scene.</summary>
/// <remarks>Publish prepass targets when SSAO or DepthNormalPrepass is required; otherwise bind
/// black and let the graph cull the pass. Set SSAO intensity to zero whenever SSAO is
/// off.</remarks>
public sealed class PrepassFeature : IRenderFeature
{
    private readonly PbrContext _ctx;
    private readonly FrustumCullingFeature _frustum;
    private FrameBlackboard? _frameBlackboard;
    private SsaoUniformsGpu _uniforms;
    private readonly ShaderProgramDesc _program;
    private readonly PipelineHandle _pipeline;
    private PipelineHandle _skinnedPipeline;
    private readonly BindGroupHandle _jointGroup;

    internal PrepassFeature(PbrContext ctx, FrustumCullingFeature frustum)
    {
        _ctx = ctx;
        _frustum = frustum;
        var renderer = ctx.Renderer;

        // Reuses the main draw ring/group (its group 0 is the same DrawUniforms, made
        // dynamic-offset). Vertex layout is position + normal over the mesh stride.
        _program = ShaderPrograms.WithDynamicDrawRing(ShaderPrograms.Load("Shaders.depthNormalPrepass"));
        _pipeline = renderer.CreatePipeline(
            _program, NormalFormat,
            depthStencilFormat: TextureFormat.Depth32Float,
            depthWriteEnabled: true,
            depthCompare: CompareFunction.Less);
        _jointGroup = renderer.CreateBindGroup(new BindGroupDesc("PbrPrepassJointGroup", ShaderPrograms.FindGroup(_program, 1), new[]
        {
            BindGroupEntryDesc.ForBuffer(0, ctx.JointBuffer, 0, ctx.JointBufferBytes),
        }));
        SsaoUniformBuffer = renderer.CreateBuffer(new BufferDesc(
            "PbrSsaoUniforms", (ulong)Unsafe.SizeOf<SsaoUniformsGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst));

        EnsureTargets();
    }

    public const TextureFormat NormalFormat = TextureFormat.Rgba16Float;

    public FeatureDefinition Definition => PbrFeatures.Prepass;
    public FrameRequirements Requires => FrameRequirements.None;

    /// <summary>Group-3 SSAO uniforms: intensity, radius, bias, power, and the screen size.</summary>
    internal BufferHandle SsaoUniformBuffer { get; }

    public void Resize(uint width, uint height) => EnsureTargets();

    /// <summary>The scene pass binds <see cref="SsaoUniformBuffer"/> every frame, whether or not
    /// this feature runs, so being switched off has to be WRITTEN there: without this the shader
    /// keeps the last enabled frame's intensity and samples the black fallback with it, which
    /// darkens every crease in the picture for as long as the feature stays off.</summary>
    public void OnEnabledChanged(bool enabled)
    {
        if (!enabled) UploadSsaoUniforms(new SsaoUniformsGpu { Screen = ScreenParams() });
    }

    private Vector4 ScreenParams() => new(1f / _ctx.Width, 1f / _ctx.Height, _ctx.Width, _ctx.Height);

    private void UploadSsaoUniforms(in SsaoUniformsGpu uniforms) =>
        _ctx.Renderer.UpdateBuffer<SsaoUniformsGpu>(
            SsaoUniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in uniforms), 1));

    private void EnsureTargets()
    {
        _ctx.Targets.Ensure(PbrTargets.PrepassNormal, _ctx.FrameTarget(NormalFormat));
        _ctx.Targets.Ensure(PbrTargets.PrepassDepth, _ctx.FrameTarget(TextureFormat.Depth32Float));
    }

    public void Setup(in FrameContext frame)
    {
        var scene = _ctx.Scene;
        var s = scene.Ssao;
        var hasOpaque = _ctx.Opaque.Count > 0;
        // Intensity 0 (SSAO off, or nothing opaque this frame) makes the shader skip its taps.
        var ssaoRuns = s.Enabled && hasOpaque;
        _frameBlackboard = frame.Blackboard;
        _uniforms = new SsaoUniformsGpu
        {
            Params = new Vector4(ssaoRuns ? s.Intensity : 0f, MathF.Max(s.Radius, 1e-3f), s.Bias, MathF.Max(s.Power, 1e-3f)),
            Screen = ScreenParams(),
            Rtao = new Vector4(0f, 0f, scene.Ssr.MaxRoughness, 0f),
        };

        var normal = frame.Graph.Texture(PbrTargets.PrepassNormal);
        var depth = frame.Graph.Texture(PbrTargets.PrepassDepth);
        frame.Graph.AddRasterPass("Prepass.DepthNormal", RenderPassEvent.Prepass)
            .Color(0, normal, LoadOp.Clear, clear: new ColorRgba(0f, 0f, 0f, 0f))
            .Depth(depth, LoadOp.Clear, clear: 1f)
            .Record(this, RecordPrepass);

        // Published whenever a consumer wants it; a frame with nothing opaque publishes nothing,
        // so a consumer binds black rather than a cleared target it would misread as geometry.
        var required = (frame.Requirements & FrameRequirements.DepthNormalPrepass) != 0;
        if (hasOpaque && (ssaoRuns || required))
        {
            frame.Blackboard.Publish(PbrResults.PrepassNormal, normal);
            frame.Blackboard.Publish(PbrResults.PrepassDepth, depth);
        }
    }

    public void BeforeSubmit()
    {
        // These producers set up after the prepass. Only sample textures they actually
        // published this frame, including switch transitions and SSR history warm-up.
        if (_frameBlackboard is null) return;
        _uniforms.Rtao.X = _frameBlackboard.TryGet(PbrResults.RayTracedAo, out _) ? 1f : 0f;
        _uniforms.Rtao.Y = _frameBlackboard.TryGet(PbrResults.SsrReflection, out _) ? 1f : 0f;
        UploadSsaoUniforms(_uniforms);
    }

    // opaque[i] uses the same dynamic offset EncodeBucket fills for it, so no extra ring space or
    // upload is needed — which is also why this recorder assumes the opaque bucket starts at slot 0.
    private static void RecordPrepass(PrepassFeature self, ref PassRecording pass, int _)
    {
        ref var encoder = ref pass.Encoder;
        var ctx = self._ctx;
        encoder.SetBindGroup(1, self._jointGroup);
        var skinnedActive = (bool?)null;
        for (var i = 0; i < ctx.Opaque.Count; i++)
        {
            if (!self._frustum.OpaqueVisible(i)) continue;
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
            _program, NormalFormat,
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
        renderer.DestroyBuffer(SsaoUniformBuffer);
    }
}
