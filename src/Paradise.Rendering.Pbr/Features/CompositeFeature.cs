using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Tonemap the current HDR scene with bloom into the backbuffer or a linear display
/// intermediate when later effects request one.</summary>
public sealed class CompositeFeature : IRenderFeature
{
    private readonly PbrContext _ctx;
    private readonly PipelineHandle _pipeline;
    private readonly PipelineHandle _linearPipeline;
    private readonly BufferHandle _uniformBuffer;
    private readonly BindGroupLayoutDesc _groupLayout;

    internal CompositeFeature(PbrContext ctx)
    {
        _ctx = ctx;
        var renderer = ctx.Renderer;
        // sRGB formats let the hardware encode (linear entry); everything else encodes in-shader.
        UsesSrgbEntryPoint = !IsSrgbFormat(renderer.ColorFormat);
        var program = ShaderPrograms.Load("Shaders.composite");
        _groupLayout = ShaderPrograms.FindGroup(program, 0);
        _pipeline = renderer.CreatePipeline(
            program, renderer.ColorFormat,
            fragmentEntryPoint: UsesSrgbEntryPoint ? "compositeFragmentSrgb" : "compositeFragment");
        _linearPipeline = renderer.CreatePipeline(program, PbrTargets.HdrFormat, fragmentEntryPoint: "compositeFragment");
        _uniformBuffer = renderer.CreateBuffer(new BufferDesc(
            "PbrCompositeUniforms", (ulong)Unsafe.SizeOf<CompositeUniformsGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst));
    }

    public FeatureDefinition Definition => PbrFeatures.Composite;
    public FrameRequirements Requires => FrameRequirements.None;

    internal bool UsesSrgbEntryPoint { get; }

    public void Resize(uint width, uint height)
    {
    }

    public void Setup(in FrameContext frame)
    {
        var scene = _ctx.Scene;
        var graph = frame.Graph;
        // The one place bloom is switched off: with nothing published the binding is black, the
        // whole chain is unreachable, and the shader still samples it scaled by zero.
        var intermediate = (frame.Requirements & FrameRequirements.DisplayColor) != 0;
        var output = FrameGraph.Backbuffer;
        if (intermediate)
        {
            _ctx.Targets.Ensure(PbrTargets.DisplayColor, _ctx.FrameTarget(PbrTargets.HdrFormat));
            output = graph.Texture(PbrTargets.DisplayColor);
            frame.Blackboard.Publish(PbrResults.DisplayColor, output);
        }
        else
        {
            _ctx.Targets.Release(PbrTargets.DisplayColor);
        }
        var hasBloom = frame.Blackboard.TryGet(PbrResults.Bloom, out var bloom);
        var uniforms = new CompositeUniformsGpu
        {
            Tone = new Vector4((float)scene.Tonemap.Mode, scene.Tonemap.Exposure, scene.Tonemap.White,
                hasBloom ? scene.Bloom.Intensity : 0f),
        };
        _ctx.Renderer.UpdateBuffer<CompositeUniformsGpu>(_uniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));

        graph.AddRasterPass("Composite", RenderPassEvent.Composite)
            .Color(0, output, LoadOp.Clear, clear: new ColorRgba(0f, 0f, 0f, 1f))
            .BindGroup(0, "PbrCompositeGroup", _groupLayout,
            [
                GraphBinding.Texture(0, frame.Blackboard.GetOrDefault(PbrResults.SceneColor, graph.Texture(PbrTargets.Hdr))),
                GraphBinding.Sampler(1, _ctx.LinearClampSampler),
                GraphBinding.Texture(2, hasBloom ? bloom : frame.Black),
                GraphBinding.Buffer(3, _uniformBuffer, 0, (ulong)Unsafe.SizeOf<CompositeUniformsGpu>()),
            ])
            .Record(this, intermediate ? RecordLinear : RecordComposite);
    }

    private static void RecordComposite(CompositeFeature self, ref PassRecording pass, int _) =>
        Fullscreen.Record(ref pass, self._pipeline);

    private static void RecordLinear(CompositeFeature self, ref PassRecording pass, int _) =>
        Fullscreen.Record(ref pass, self._linearPipeline);

    private static bool IsSrgbFormat(TextureFormat format) =>
        format is TextureFormat.Rgba8UnormSrgb or TextureFormat.Bgra8UnormSrgb;

    public void Dispose()
    {
        _ctx.Renderer.DestroyPipeline(_pipeline);
        _ctx.Renderer.DestroyPipeline(_linearPipeline);
        _ctx.Renderer.DestroyBuffer(_uniformBuffer);
    }
}
