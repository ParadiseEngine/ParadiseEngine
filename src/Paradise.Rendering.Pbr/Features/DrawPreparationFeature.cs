using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Prepares captured draws before culling and raster passes consume their final slots.</summary>
public sealed class DrawPreparationFeature : IRenderFeature
{
    private readonly PbrContext _ctx;
    internal DrawPreparationFeature(PbrContext ctx) => _ctx = ctx;

    public FeatureDefinition Definition => PbrFeatures.DrawPreparation;
    public FrameRequirements Requires => FrameRequirements.None;

    public void Setup(in FrameContext frame)
    {
        var instancing = _ctx.Frame.Instancing;
        if (_ctx.Frame.InstancingEnabled && instancing.PackAndRegroup)
            _ctx.Frame.PreparePackedAndRegrouped(_ctx.Materials, _ctx.DrawCapacity, _ctx.DrawStaging, (int)_ctx.DrawStride);
        else
            _ctx.Frame.PrepareDirect(_ctx.DrawStaging, (int)_ctx.DrawStride);
    }

    public void Resize(uint width, uint height) { }
    public void Dispose() { }
}
