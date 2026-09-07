using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Screen-space reflection at <see cref="RenderPassEvent.AfterPrepass"/>: a compute pass
/// marches each pixel's mirror ray through the depth + normal pre-pass and writes the color the
/// PREVIOUS frame's HDR scene held at the hit, with a confidence the scene pass blends its indirect
/// specular by. The history is this feature's own copy of the HDR target, blitted at
/// <see cref="RenderPassEvent.AfterTransparent"/>; reading last frame's picture is what lets the
/// reflection feed the very pass that produces it, at the cost of one frame of latency.
///
/// <para>Runs while <see cref="PbrScene.Ssr"/> is enabled and something opaque exists; it requires
/// the pre-pass through <see cref="FrameRequirements.DepthNormalPrepass"/> and publishes
/// <see cref="PbrResults.SsrReflection"/>. Off, it declares nothing.</para></summary>
public sealed class ScreenSpaceReflectionFeature : IRenderFeature
{
    [StructLayout(LayoutKind.Sequential, Size = 240)]
    private struct SsrUniformsGpu
    {
        public Vector4 Params;
        public Vector4 Screen;
        public Vector4 Camera;
        public Matrix4x4 ViewProj;
        public Matrix4x4 InvViewProj;
        public Matrix4x4 PrevViewProj;
    }

    private readonly PbrContext _ctx;
    private readonly ComputePipelineHandle _pipeline;
    private readonly BindGroupLayoutDesc _group0;
    private readonly PipelineHandle _historyPipeline;
    private readonly BindGroupLayoutDesc _historyGroup;
    private readonly BufferHandle _uniformBuffer;
    private Matrix4x4 _previousViewProjection = Matrix4x4.Identity;
    private bool _historyValid;
    private uint _frame;
    private uint _targetWidth;
    private uint _targetHeight;
    private float _scale = 1f;

    internal ScreenSpaceReflectionFeature(PbrContext ctx)
    {
        _ctx = ctx;
        var program = ShaderPrograms.Load("Shaders.ssr");
        _pipeline = ctx.Renderer.CreateComputePipeline(program);
        _group0 = ShaderPrograms.FindGroup(program, 0);
        var blit = ShaderPrograms.Load("Shaders.blit");
        _historyGroup = ShaderPrograms.FindGroup(blit, 0);
        _historyPipeline = ctx.Renderer.CreatePipeline(blit, PbrTargets.HdrFormat);
        _uniformBuffer = ctx.Renderer.CreateBuffer(new BufferDesc(
            "PbrSsrUniforms", (ulong)Unsafe.SizeOf<SsrUniformsGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst));
    }

    /// <summary>Whether last frame's HDR copy exists to read: false before the first frame the
    /// feature ran and in the frame after a resize. The pre-pass reads it to tell the scene shader
    /// whether the bound reflection texture is real.</summary>
    internal bool HistoryReady => _historyValid;

    public FeatureDefinition Definition => PbrFeatures.ScreenSpaceReflection;
    public FrameRequirements Requires =>
        _ctx.Scene.Ssr.Enabled ? FrameRequirements.DepthNormalPrepass : FrameRequirements.None;

    public void Resize(uint width, uint height)
    {
        // Targets exist only once the feature has run: two frame-sized textures, one of them
        // HDR, are not worth holding for a renderer that never reflects. Setup re-ensures them
        // at the new size; a resized history holds nothing this frame either way.
        if (_ctx.Targets.Contains(PbrTargets.SsrHistory)) EnsureTargets(_scale);
        _historyValid = false;
    }

    /// <summary>The pre-pass asks <see cref="HistoryReady"/> whether the reflection texture the
    /// scene binds holds anything; switched off, this feature stops copying the frame but the
    /// last copy is still there, so the answer has to be retracted here rather than in a
    /// <see cref="Setup"/> that no longer runs.</summary>
    public void OnEnabledChanged(bool enabled)
    {
        if (!enabled) _historyValid = false;
    }

    private void EnsureTargets(float scale)
    {
        _scale = Math.Clamp(scale, 0.1f, 1f);
        _targetWidth = Math.Max(1, (uint)MathF.Ceiling(_ctx.Width * _scale));
        _targetHeight = Math.Max(1, (uint)MathF.Ceiling(_ctx.Height * _scale));
        _ctx.Targets.Ensure(PbrTargets.SsrReflection, new TextureDesc(
            null, _targetWidth, _targetHeight, 1, 1, 1, TextureDimension.D2, TextureFormat.Rgba16Float,
            TextureUsage.StorageBinding | TextureUsage.TextureBinding));
        _ctx.Targets.Ensure(PbrTargets.SsrHistory, _ctx.FrameTarget(PbrTargets.HdrFormat));
    }

    public void Setup(in FrameContext frame)
    {
        var settings = _ctx.Scene.Ssr;
        if (!settings.Enabled)
        {
            _historyValid = false;
            return;
        }
        var graph = frame.Graph;
        var hasPrepass = frame.Blackboard.TryGet(PbrResults.PrepassNormal, out var normal)
            & frame.Blackboard.TryGet(PbrResults.PrepassDepth, out var depth);

        // The history copy is declared whenever the feature is on, so the first frame with
        // something opaque already has last frame's picture to read. Its consumer is NEXT frame's
        // trace, which the graph cannot see: the pass is kept explicitly, and so is its STORE —
        // inferred, the graph discards an attachment nothing reads this frame, which is exactly
        // the first frame and the frame after a resize, the two that exist to fill it.
        EnsureTargets(settings.ResolutionScale);
        var history = graph.Texture(PbrTargets.SsrHistory);
        graph.AddRasterPass("Ssr.History", RenderPassEvent.AfterTransparent)
            .NeverCull()
            .Color(0, history, LoadOp.Clear, StoreOp.Store, clear: new ColorRgba(0f, 0f, 0f, 0f))
            .BindGroup(0, "PbrSsrHistoryGroup", _historyGroup,
            [
                GraphBinding.Texture(0, graph.Texture(PbrTargets.Hdr)),
                GraphBinding.Sampler(1, _ctx.LinearClampSampler),
                GraphBinding.Texture(2, graph.Texture(PbrTargets.Depth)),
            ])
            .Record(this, RecordHistory);

        var viewProjection = _ctx.ViewProjection;
        if (hasPrepass && _historyValid)
        {
            var uniforms = new SsrUniformsGpu
            {
                Params = new Vector4(Math.Clamp(settings.MaxSteps, 1, 256), MathF.Max(settings.MaxDistance, 1e-3f),
                    MathF.Max(settings.Thickness, 1e-3f), MathF.Max(settings.Intensity, 0f)),
                Screen = new Vector4(_targetWidth, _targetHeight, _ctx.Width, _ctx.Height),
                Camera = new Vector4(_ctx.Scene.Camera.Position, _frame++),
                ViewProj = viewProjection,
                InvViewProj = Matrix4x4.Invert(viewProjection, out var inv) ? inv : Matrix4x4.Identity,
                PrevViewProj = _previousViewProjection,
            };
            _ctx.Renderer.UpdateBuffer<SsrUniformsGpu>(_uniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));

            var output = graph.Texture(PbrTargets.SsrReflection);
            // The history is bound as a raw view and declared as a history read: it is written
            // later this frame, which a plain read edge would rightly refuse.
            graph.AddComputePass("Ssr.Trace", RenderPassEvent.AfterPrepass)
                .BindGroup(0, "PbrSsrGroup", _group0,
                [
                    GraphBinding.Buffer(0, _uniformBuffer, 0, (ulong)Unsafe.SizeOf<SsrUniformsGpu>()),
                    GraphBinding.Texture(1, normal),
                    GraphBinding.Texture(2, depth),
                    GraphBinding.View(3, _ctx.Targets.View(PbrTargets.SsrHistory)),
                    GraphBinding.Sampler(4, _ctx.LinearClampSampler),
                    GraphBinding.StorageTexture(5, output),
                ])
                .ReadsHistory(history)
                .Record(this, RecordTrace);
            frame.Blackboard.Publish(PbrResults.SsrReflection, output);
        }

        _previousViewProjection = viewProjection;
        _historyValid = true;
    }

    private static void RecordHistory(ScreenSpaceReflectionFeature self, ref PassRecording pass, int _) =>
        Fullscreen.Record(ref pass, self._historyPipeline);

    private static void RecordTrace(ScreenSpaceReflectionFeature self, ref PassRecording pass, int _)
    {
        pass.Encoder.SetComputePipeline(self._pipeline);
        pass.SetBindGroup(0);
        pass.Encoder.Dispatch(new DispatchCommand((self._targetWidth + 7) / 8, (self._targetHeight + 7) / 8, 1));
    }

    public void Dispose()
    {
        _ctx.Renderer.DestroyComputePipeline(_pipeline);
        _ctx.Renderer.DestroyPipeline(_historyPipeline);
        _ctx.Renderer.DestroyBuffer(_uniformBuffer);
    }
}
