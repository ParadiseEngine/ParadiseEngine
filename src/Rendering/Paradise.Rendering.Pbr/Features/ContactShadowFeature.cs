using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Requests the depth prepass for inline raster contact shadows. Frame-local publication
/// gates sampling, so disabling either this feature or the prepass retracts the effect immediately.</summary>
public sealed class ContactShadowFeature : IRenderFeature
{
    private readonly PbrContext _ctx;
    internal const string Result = "PbrContactShadowDepth";
    internal ContactShadowFeature(PbrContext ctx) => _ctx = ctx;
    public FeatureDefinition Definition => PbrFeatures.ContactShadows;
    public FrameRequirements Requires => _ctx.Scene.ContactShadows.Enabled ? FrameRequirements.DepthNormalPrepass : FrameRequirements.None;
    public void Setup(in FrameContext frame)
    {
        if (_ctx.Scene.ContactShadows.Enabled && frame.Blackboard.TryGet(PbrResults.PrepassDepth, out var depth))
            frame.Blackboard.Publish(Result, depth);
    }
    public void Resize(uint width, uint height) { }
    public void Dispose() { }
}
