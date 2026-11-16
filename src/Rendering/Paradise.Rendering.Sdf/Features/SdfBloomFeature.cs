using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Sdf;

/// <summary>Bloom as a progressive dual-filter chain: a bright pass into a half-resolution base,
/// downsamples halving to about <see cref="MinDimension"/>, and additive upsamples back to the
/// base, which the composite adds to the scene.</summary>
public sealed class SdfBloomFeature : IRenderFeature
{
    private const uint MinDimension = 8;

    private readonly SdfContext _ctx;
    private readonly PipelineHandle _brightPipeline;
    private readonly PipelineHandle _downPipeline;
    private readonly PipelineHandle _upPipeline;
    private readonly BufferHandle _uniformBuffer;
    private readonly BindGroupLayoutDesc _layout;
    private readonly GraphTexture[] _levelTextures = new GraphTexture[SdfTargets.BloomLevels];
    private int _levels;

    internal SdfBloomFeature(SdfContext ctx)
    {
        _ctx = ctx;
        var renderer = ctx.Renderer;
        var program = SdfContext.LoadProgram("Shaders.sdfBloom");
        SdfUniformLayout.ValidateBloom(program);
        _layout = SdfContext.FindGroup(program, 0);
        _uniformBuffer = renderer.CreateBuffer(new BufferDesc("SdfBloomUniforms", UniformBytes, BufferUsage.Uniform | BufferUsage.CopyDst));
        _brightPipeline = renderer.CreatePipeline(program, SdfTargets.HdrFormat, fragmentEntryPoint: "brightFragment");
        _downPipeline = renderer.CreatePipeline(program, SdfTargets.HdrFormat, fragmentEntryPoint: "downsampleFragment");
        _upPipeline = renderer.CreatePipeline(program, SdfTargets.HdrFormat, blend: BlendMode.Additive, fragmentEntryPoint: "upsampleFragment");
    }

    private static ulong UniformBytes => (ulong)Unsafe.SizeOf<SdfBloomUniformsGpu>();

    public FeatureDefinition Definition => SdfFeatures.Bloom;
    public FrameRequirements Requires => FrameRequirements.None;

    public void Resize(uint width, uint height)
    {
        var w = Math.Max(1, _ctx.Width / 2);
        var h = Math.Max(1, _ctx.Height / 2);
        _levels = 0;
        while (_levels < SdfTargets.BloomLevels)
        {
            _ctx.Targets.Ensure(SdfTargets.Bloom[_levels], SdfContext.Target(w, h, SdfTargets.HdrFormat));
            _levels++;
            if (w <= MinDimension || h <= MinDimension) break;
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
        for (var i = _levels; i < SdfTargets.BloomLevels; i++) _ctx.Targets.Release(SdfTargets.Bloom[i]);
    }

    public void Setup(in FrameContext frame)
    {
        var settings = _ctx.Scene.Bloom;
        if (!settings.Enabled || _levels < 2 || !frame.Blackboard.TryGet(SdfResults.SceneColor, out var hdr)) return;

        var uniforms = new SdfBloomUniformsGpu
        {
            Threshold = new Vector4(
                float.IsFinite(settings.Threshold) ? MathF.Max(settings.Threshold, 0f) : 1f,
                float.IsFinite(settings.Knee) ? MathF.Max(settings.Knee, 0f) : 0.5f, 0f, 0f),
        };
        _ctx.Renderer.UpdateBuffer<SdfBloomUniformsGpu>(_uniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));

        var graph = frame.Graph;
        for (var i = 0; i < _levels; i++) _levelTextures[i] = graph.Texture(SdfTargets.Bloom[i]);

        // Bright into level 0, each downsample into the next level, then each upsample added onto
        // the level above it, so level 0 ends as the sum of every blur radius.
        var black = new ColorRgba(0f, 0f, 0f, 1f);
        Declare(graph.AddRasterPass("Sdf.Bloom.Bright", RenderPassEvent.Post)
            .Color(0, _levelTextures[0], LoadOp.Clear, clear: black), hdr).Record(this, RecordBright);
        for (var i = 0; i < _levels - 1; i++)
        {
            Declare(graph.AddRasterPass("Sdf.Bloom.Down", RenderPassEvent.Post)
                .Color(0, _levelTextures[i + 1], LoadOp.Clear, clear: black), _levelTextures[i]).Record(this, RecordDown);
        }
        for (var i = _levels - 1; i > 0; i--)
        {
            Declare(graph.AddRasterPass("Sdf.Bloom.Up", RenderPassEvent.Post)
                .Color(0, _levelTextures[i - 1], LoadOp.Load), _levelTextures[i]).Record(this, RecordUp);
        }
        frame.Blackboard.Publish(SdfResults.Bloom, _levelTextures[0]);
    }

    private FrameGraph.PassBuilder Declare(FrameGraph.PassBuilder pass, GraphTexture source) =>
        pass.BindGroup(0, "SdfBloom", _layout,
        [
            GraphBinding.Texture(0, source),
            GraphBinding.Sampler(1, _ctx.LinearClampSampler),
            GraphBinding.Buffer(2, _uniformBuffer, 0, UniformBytes),
        ]);

    private static void RecordBright(SdfBloomFeature self, ref PassRecording pass, int _) => SdfContext.DrawFullscreen(ref pass, self._brightPipeline);
    private static void RecordDown(SdfBloomFeature self, ref PassRecording pass, int _) => SdfContext.DrawFullscreen(ref pass, self._downPipeline);
    private static void RecordUp(SdfBloomFeature self, ref PassRecording pass, int _) => SdfContext.DrawFullscreen(ref pass, self._upPipeline);

    public void Dispose()
    {
        var renderer = _ctx.Renderer;
        renderer.DestroyPipeline(_brightPipeline);
        renderer.DestroyPipeline(_downPipeline);
        renderer.DestroyPipeline(_upPipeline);
        renderer.DestroyBuffer(_uniformBuffer);
    }
}
