using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Applies the output transfer function after all linear display effects.</summary>
public sealed class PresentationFeature : IRenderFeature
{
    private readonly PbrContext _ctx;
    private readonly PipelineHandle _pipeline;
    private readonly BindGroupLayoutDesc _layout;

    internal PresentationFeature(PbrContext ctx)
    {
        _ctx = ctx;
        var program = ShaderPrograms.Load("Shaders.presentation");
        _layout = ShaderPrograms.FindGroup(program, 0);
        var srgb = ctx.Renderer.ColorFormat is TextureFormat.Rgba8UnormSrgb or TextureFormat.Bgra8UnormSrgb;
        _pipeline = ctx.Renderer.CreatePipeline(program, ctx.Renderer.ColorFormat,
            fragmentEntryPoint: srgb ? "presentLinear" : "presentSrgb");
    }

    public FeatureDefinition Definition => PbrFeatures.Presentation;
    public FrameRequirements Requires => FrameRequirements.None;
    public void Resize(uint width, uint height) { }

    public void Setup(in FrameContext frame)
    {
        if (!frame.Blackboard.TryGet(PbrResults.DisplayColor, out var color)) return;
        frame.Graph.AddRasterPass("Presentation", RenderPassEvent.AfterComposite, 50)
            .Color(0, FrameGraph.Backbuffer, LoadOp.Clear, clear: new ColorRgba(0f, 0f, 0f, 1f))
            .BindGroup(0, "PbrPresentation", _layout,
            [
                GraphBinding.Texture(0, color),
                GraphBinding.Sampler(1, _ctx.LinearClampSampler),
            ])
            .Record(this, Record);
    }

    private static void Record(PresentationFeature self, ref PassRecording pass, int _) =>
        Fullscreen.Record(ref pass, self._pipeline);

    public void Dispose() => _ctx.Renderer.DestroyPipeline(_pipeline);
}
