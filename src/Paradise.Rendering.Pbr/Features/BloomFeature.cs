using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Bloom as a progressive dual-filter mip chain (COD-style): a bright pass into a
/// half-resolution base, a downsample chain halving to ~<see cref="MinDim"/>, and an additive
/// upsample chain back to mip 0, all at <see cref="RenderPassEvent.Post"/>.
///
/// <para>The chain is declared every frame whether or not bloom is on. It is reachable only
/// through mip 0, published as <see cref="PbrResults.Bloom"/> in the frames the effect runs;
/// when it is not published the composite binds black and the graph culls all 2L−1 passes.</para></summary>
public sealed class BloomFeature : IRenderFeature
{
    private const int MaxLevels = 6;
    private const uint MinDim = 8;

    private readonly PbrContext _ctx;
    private readonly PipelineHandle _brightPipeline;
    private readonly PipelineHandle _downPipeline;
    private readonly PipelineHandle _upPipeline;
    private readonly BufferHandle _uniformBuffer;
    private readonly BindGroupLayoutDesc _groupLayout;
    private readonly List<GraphTexture> _mips = [];
    private int _levels;

    internal BloomFeature(PbrContext ctx)
    {
        _ctx = ctx;
        var renderer = ctx.Renderer;
        // Pipelines built once; they share one bind-group layout: source texture + sampler + params.
        var program = ShaderPrograms.Load("Shaders.bloom");
        _groupLayout = ShaderPrograms.FindGroup(program, 0);
        _uniformBuffer = renderer.CreateBuffer(new BufferDesc(
            "PbrBloomUniforms", (ulong)Unsafe.SizeOf<CompositeUniformsGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst));
        _brightPipeline = renderer.CreatePipeline(program, PbrTargets.HdrFormat, fragmentEntryPoint: "brightFragment");
        _downPipeline = renderer.CreatePipeline(program, PbrTargets.HdrFormat, fragmentEntryPoint: "downsampleFragment");
        _upPipeline = renderer.CreatePipeline(program, PbrTargets.HdrFormat, blend: BlendMode.Additive, fragmentEntryPoint: "upsampleFragment");
        EnsureChain();
    }

    public FeatureDefinition Definition => PbrFeatures.Bloom;
    public FrameRequirements Requires => FrameRequirements.None;

    public void Resize(uint width, uint height) => EnsureChain();

    // A half-res base halving down to ~MinDim (≤ MaxLevels levels). Each level is an Rgba16Float
    // render target sampled by the next pass.
    private void EnsureChain()
    {
        var sizes = new List<(uint W, uint H)>();
        uint w = Math.Max(1, _ctx.Width / 2), h = Math.Max(1, _ctx.Height / 2);
        for (var i = 0; i < MaxLevels; i++)
        {
            sizes.Add((w, h));
            if (w <= MinDim || h <= MinDim) break;
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
        _levels = sizes.Count;
        for (var i = 0; i < _levels; i++)
            _ctx.Targets.Ensure(PbrTargets.Bloom[i], PbrTargets.RenderTarget(sizes[i].W, sizes[i].H, PbrTargets.HdrFormat));
        for (var i = _levels; i < MaxLevels; i++)
            _ctx.Targets.Release(PbrTargets.Bloom[i]);
    }

    public void Setup(in FrameContext frame)
    {
        var scene = _ctx.Scene;
        var graph = frame.Graph;
        var hdr = frame.Blackboard.GetOrDefault(PbrResults.SceneColor, graph.Texture(PbrTargets.Hdr));
        _mips.Clear();
        for (var i = 0; i < _levels; i++)
            _mips.Add(graph.Texture(PbrTargets.Bloom[i]));

        // bright → mip 0; downsample i → mip i+1 (Clear); additive upsample j → mip L-2-j (Load, so
        // the tent blur accumulates onto the down-mip content).
        var black = new ColorRgba(0f, 0f, 0f, 1f);
        Declare(graph.AddRasterPass("Bloom.Bright", RenderPassEvent.Post)
            .Color(0, _mips[0], LoadOp.Clear, clear: black), hdr)
            .Record(this, RecordBright);
        for (var i = 0; i < _levels - 1; i++)
        {
            Declare(graph.AddRasterPass("Bloom.Down", RenderPassEvent.Post)
                .Color(0, _mips[i + 1], LoadOp.Clear, clear: black), _mips[i])
                .Record(this, RecordDown);
        }
        for (var j = 0; j < _levels - 1; j++)
        {
            Declare(graph.AddRasterPass("Bloom.Up", RenderPassEvent.Post)
                .Color(0, _mips[_levels - 2 - j], LoadOp.Load, clear: black), _mips[_levels - 1 - j])
                .Record(this, RecordUp);
        }

        if (scene.Bloom.Enabled && _levels > 1)
        {
            var uniforms = new CompositeUniformsGpu { Tone = new Vector4(scene.Bloom.Threshold, scene.Bloom.Knee, 0f, 0f) };
            _ctx.Renderer.UpdateBuffer<CompositeUniformsGpu>(_uniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));
            frame.Blackboard.Publish(PbrResults.Bloom, _mips[0]);
        }
    }

    private FrameGraph.PassBuilder Declare(FrameGraph.PassBuilder pass, GraphTexture source) =>
        pass.BindGroup(0, "PbrBloomGroup", _groupLayout,
        [
            GraphBinding.Texture(0, source),
            GraphBinding.Sampler(1, _ctx.LinearClampSampler),
            GraphBinding.Buffer(2, _uniformBuffer, 0, (ulong)Unsafe.SizeOf<CompositeUniformsGpu>()),
        ]);

    private static void RecordBright(BloomFeature self, ref PassRecording pass, int _) => Fullscreen.Record(ref pass, self._brightPipeline);
    private static void RecordDown(BloomFeature self, ref PassRecording pass, int _) => Fullscreen.Record(ref pass, self._downPipeline);
    private static void RecordUp(BloomFeature self, ref PassRecording pass, int _) => Fullscreen.Record(ref pass, self._upPipeline);

    public void Dispose()
    {
        var renderer = _ctx.Renderer;
        renderer.DestroyPipeline(_brightPipeline);
        renderer.DestroyPipeline(_downPipeline);
        renderer.DestroyPipeline(_upPipeline);
        renderer.DestroyBuffer(_uniformBuffer);
    }
}
