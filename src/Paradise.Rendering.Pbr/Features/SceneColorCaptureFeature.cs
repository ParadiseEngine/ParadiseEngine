using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Opt-in scene-color capture: the opaque scene is blitted, linear HDR, into an
/// exported texture at <see cref="RenderPassEvent.SceneColorCapture"/>, so a blend material
/// (water) can sample what is BEHIND it for screen-space refraction.
///
/// <para>Requiring <see cref="FrameRequirements.SceneColorCapture"/> is what splits the scene
/// pass at the opaque/blend boundary; this feature never touches that pass. Costs one fullscreen
/// blit plus a color+depth reload per frame while enabled.</para></summary>
public sealed class SceneColorCaptureFeature : IRenderFeature
{
    private readonly PbrContext _ctx;
    private PipelineHandle _pipeline;
    private BindGroupLayoutDesc? _groupLayout;
    private bool _enabled;

    internal SceneColorCaptureFeature(PbrContext ctx)
    {
        _ctx = ctx;
    }

    public FeatureDefinition Definition => PbrFeatures.SceneColorCapture;
    public FrameRequirements Requires => FrameRequirements.SceneColorCapture;

    /// <summary>The target exists exactly while the switch is on, and
    /// <see cref="ViewChanged"/> fires on the transition — synchronously, on the thread that
    /// flipped it, so a host can switch capture on and then create the materials that bind
    /// <see cref="View"/>.</summary>
    public void OnEnabledChanged(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (enabled)
        {
            EnsurePipeline();
            EnsureTarget();
        }
        else
        {
            _ctx.Targets.Release(PbrTargets.SceneColor);
        }
        // The disable path fires the event too — the view is INVALID inside the handler, and
        // any material still bound to the old view must unbind or repoint (a bind group
        // referencing the destroyed view is a Dawn validation error on its next SetBindGroup).
        ViewChanged?.Invoke();
    }

    /// <summary>The captured opaque scene: rgb is the opaque+sky color, ALPHA is the opaque
    /// scene's device depth. Invalid while disabled; recreated on resize.</summary>
    public TextureViewHandle View =>
        _ctx.Targets.Contains(PbrTargets.SceneColor) ? _ctx.Targets.View(PbrTargets.SceneColor) : default;

    /// <summary>Raised whenever <see cref="View"/> changes: enabled, resized, or disabled (the
    /// view is invalid in the handler).</summary>
    public event Action? ViewChanged;

    public void Resize(uint width, uint height)
    {
        if (!_enabled) return;
        EnsureTarget();
        ViewChanged?.Invoke();
    }

    private void EnsurePipeline()
    {
        if (_pipeline.IsValid) return;
        // One-time: the blit program/pipeline survive capture toggles (pipelines are cheap to
        // keep, expensive to churn).
        var program = ShaderPrograms.Load("Shaders.blit");
        _groupLayout = ShaderPrograms.FindGroup(program, 0);
        _pipeline = _ctx.Renderer.CreatePipeline(program, PbrTargets.HdrFormat); // linear HDR, no depth
    }

    private void EnsureTarget()
    {
        _ctx.Targets.Ensure(PbrTargets.SceneColor, _ctx.FrameTarget(PbrTargets.HdrFormat));
        // Public surface: a game's blend material samples it, so the graph must never cull its
        // producer on the grounds that no pass of its own reads it.
        _ctx.Targets.Export(PbrTargets.SceneColor);
    }

    public void Setup(in FrameContext frame)
    {
        var graph = frame.Graph;
        graph.AddRasterPass("SceneColor.Blit", RenderPassEvent.SceneColorCapture)
            .Color(0, graph.Texture(PbrTargets.SceneColor), LoadOp.Clear, clear: new ColorRgba(0f, 0f, 0f, 0f))
            .BindGroup(0, "PbrSceneBlitGroup", _groupLayout!,
            [
                GraphBinding.Texture(0, graph.Texture(PbrTargets.Hdr)),
                GraphBinding.Sampler(1, _ctx.LinearClampSampler),
                GraphBinding.Texture(2, graph.Texture(PbrTargets.Depth)),
            ])
            .Record(this, RecordBlit);
    }

    private static void RecordBlit(SceneColorCaptureFeature self, ref PassRecording pass, int _) =>
        Fullscreen.Record(ref pass, self._pipeline);

    public void Dispose()
    {
        _ctx.Targets.Release(PbrTargets.SceneColor);
        if (_pipeline.IsValid) _ctx.Renderer.DestroyPipeline(_pipeline);
    }
}
