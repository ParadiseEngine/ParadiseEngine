using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Sdf;

/// <summary>Keeps a grid of irradiance probes lit by tracing the field from each of them.</summary>
/// <remarks>Two passes a frame, both before the scene: one traces every probe's rays, a texel
/// each, and one folds them into the probe atlas, blending with last frame's. The atlas is two
/// textures written in turn, so a frame reads the one before it. The scene pass finds the atlas
/// under <see cref="SdfResults.Probes"/>; in a frame this feature does not run it lights from the
/// sky alone.</remarks>
public sealed class SdfGiFeature : IRenderFeature
{
    private readonly SdfContext _ctx;
    private readonly PipelineHandle _tracePipeline;
    private readonly PipelineHandle _blendPipeline;
    private readonly BindGroupLayoutDesc _traceLayout;
    private readonly BindGroupLayoutDesc _blendLayout;
    private SdfProbeGrid _grid;
    private int _rays;
    private int _current;
    private int _frame;
    private bool _hasHistory;

    internal SdfGiFeature(SdfContext ctx)
    {
        _ctx = ctx;
        var renderer = ctx.Renderer;
        var trace = SdfContext.LoadProgram("Shaders.sdfProbeTrace");
        SdfUniformLayout.ValidateScene(trace, probes: true);
        _traceLayout = SdfContext.FindGroup(trace, 0);
        _tracePipeline = renderer.CreatePipeline(trace, SdfTargets.ProbeFormat);
        var blend = SdfContext.LoadProgram("Shaders.sdfProbeBlend");
        SdfUniformLayout.ValidateGi(blend);
        _blendLayout = SdfContext.FindGroup(blend, 0);
        _blendPipeline = renderer.CreatePipeline(blend, SdfTargets.ProbeFormat);
    }

    public FeatureDefinition Definition => SdfFeatures.Gi;
    public FrameRequirements Requires => FrameRequirements.None;

    /// <summary>The grid the last frame this feature ran in updated; empty before the first.</summary>
    public SdfProbeGrid Grid => _grid;

    /// <summary>Rays traced by the last frame this feature ran in.</summary>
    public int RaysTraced { get; private set; }

    public void Resize(uint width, uint height)
    {
    }

    // Probes that sat out some frames describe a scene that may have moved on.
    public void OnEnabledChanged(bool enabled)
    {
        _hasHistory = false;
        RaysTraced = 0;
    }

    public void Setup(in FrameContext frame)
    {
        var settings = _ctx.Scene.Gi;
        if (!settings.Enabled)
        {
            _hasHistory = false;
            RaysTraced = 0;
            return;
        }

        var grid = SdfGiMath.Fit(settings);
        var rays = Math.Clamp(settings.RaysPerProbe, 4, SdfGiMath.MaxRaysPerProbe);
        if (grid != _grid || rays != _rays)
        {
            // Moved or resized probes hold light from somewhere else: start over.
            _grid = grid;
            _rays = rays;
            _hasHistory = false;
            _ctx.Targets.Ensure(SdfTargets.ProbeRays, SdfContext.Target((uint)rays, (uint)grid.Count, SdfTargets.ProbeFormat));
            foreach (var atlas in SdfTargets.Probes)
                _ctx.Targets.Ensure(atlas, SdfContext.Target(grid.AtlasWidth, grid.AtlasHeight, SdfTargets.ProbeFormat));
        }
        RaysTraced = rays * grid.Count;

        var data = _ctx.SceneData;
        data.Upload(_ctx);
        var uniforms = new SdfGiUniformsGpu
        {
            Origin = new Vector4(grid.Origin, grid.Spacing),
            Counts = new Vector4(grid.CountX, grid.CountY, grid.CountZ, grid.PerRow),
            Params = new Vector4(
                float.IsFinite(settings.Intensity) ? MathF.Max(settings.Intensity, 0f) : 1f,
                settings.Visibility ? 1f : 0f,
                NormalBias * grid.Spacing,
                rays),
            Update = new Vector4(
                _hasHistory && float.IsFinite(settings.Hysteresis) ? 1f - Math.Clamp(settings.Hysteresis, 0f, 0.99f) : 1f,
                float.IsFinite(settings.MaxRayDistance) ? MathF.Max(settings.MaxRayDistance, grid.Spacing) : 40f,
                ClearanceForValidity * grid.Spacing,
                _hasHistory && float.IsFinite(settings.Bounce) ? Math.Clamp(settings.Bounce, 0f, 1f) : 0f),
            RayRotation = SdfGiMath.FrameRotation(_frame),
        };
        _ctx.Renderer.UpdateBuffer<SdfGiUniformsGpu>(_ctx.GiUniforms, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));

        var previousName = SdfTargets.Probes[_current];
        _current ^= 1;
        var graph = frame.Graph;
        var previous = graph.Texture(previousName);
        var previousView = _ctx.Targets.View(previousName);
        var target = graph.Texture(SdfTargets.Probes[_current]);
        var rayTexture = graph.Texture(SdfTargets.ProbeRays);
        var black = new ColorRgba(0f, 0f, 0f, 0f);

        // Last frame's atlas is bound by view and declared as history: this frame has no pass
        // that writes it, and its bounce gain is zero until it holds something.
        graph.AddRasterPass("Sdf.ProbeTrace", RenderPassEvent.GlobalIllumination)
            .Color(0, rayTexture, LoadOp.Clear, clear: black)
            .BindGroup(0, "SdfProbeTrace", _traceLayout,
            [
                GraphBinding.Buffer(0, data.FrameBuffer, 0, SdfSceneData.FrameBytes),
                GraphBinding.Buffer(1, data.ShapeBuffer, 0, SdfSceneData.ShapeBytes),
                GraphBinding.View(2, previousView),
                GraphBinding.Buffer(3, _ctx.GiUniforms, 0, SdfContext.GiBytes),
            ])
            .ReadsHistory(previous)
            .Record(this, RecordTrace);
        // The atlas is next frame's history whether or not anything reads it this frame: with the
        // scene or the composite switched off nothing does, and the graph would cull the blend,
        // or keep it and infer a discarding store, while this feature still swaps atlases and
        // counts history.
        graph.AddRasterPass("Sdf.ProbeBlend", RenderPassEvent.GlobalIllumination, offset: 1)
            .NeverCull()
            .Color(0, target, LoadOp.Clear, StoreOp.Store, black)
            .BindGroup(0, "SdfProbeBlend", _blendLayout,
            [
                GraphBinding.Texture(0, rayTexture),
                GraphBinding.View(1, previousView),
                GraphBinding.Buffer(2, _ctx.GiUniforms, 0, SdfContext.GiBytes),
            ])
            .ReadsHistory(previous)
            .Record(this, RecordBlend);
        frame.Blackboard.Publish(SdfResults.Probes, target);
        _hasHistory = true;
        _frame++;
    }

    // Fractions of the probe spacing: how far a lookup steps off its surface before weighing the
    // probes around it, and how far from every surface a probe is fully trusted.
    private const float NormalBias = 0.2f;
    private const float ClearanceForValidity = 0.05f;

    private static void RecordTrace(SdfGiFeature self, ref PassRecording pass, int _) =>
        SdfContext.DrawFullscreen(ref pass, self._tracePipeline);

    private static void RecordBlend(SdfGiFeature self, ref PassRecording pass, int _) =>
        SdfContext.DrawFullscreen(ref pass, self._blendPipeline);

    public void Dispose()
    {
        _ctx.Renderer.DestroyPipeline(_tracePipeline);
        _ctx.Renderer.DestroyPipeline(_blendPipeline);
    }
}
