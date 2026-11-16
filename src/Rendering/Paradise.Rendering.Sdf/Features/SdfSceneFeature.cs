using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Sdf;

/// <summary>Sphere-traces the scene's distance field into HDR color and device depth.</summary>
/// <remarks>The pass has one program per combination of its two optional parts, light from the
/// irradiance probes and traced reflections, and each frame runs the one it needs. Code that
/// marches the field costs frame time even where a uniform switches it off, so a frame without
/// probes or reflections runs a program that does not contain them.</remarks>
public sealed class SdfSceneFeature : IRenderFeature
{
    private static readonly string[] s_programs =
    [
        "Shaders.sdfScene", "Shaders.sdfSceneReflections", "Shaders.sdfSceneProbes", "Shaders.sdfSceneProbesReflections",
    ];

    private readonly SdfContext _ctx;
    private readonly Variant?[] _variants = new Variant?[s_programs.Length];

    internal SdfSceneFeature(SdfContext ctx)
    {
        _ctx = ctx;
        // The plain program up front, so a broken shader fails construction; the others when a
        // scene first asks for them.
        Ensure(0);
    }

    public FeatureDefinition Definition => SdfFeatures.Scene;
    public FrameRequirements Requires => FrameRequirements.None;

    /// <summary>Shapes traced by the last frame this feature ran in.</summary>
    public int ShapeCount { get; private set; }

    /// <summary>Lights shaded by the last frame this feature ran in.</summary>
    public int LightCount { get; private set; }

    /// <summary>Whether the last frame this feature ran in lit its ambient term from the probes.</summary>
    public bool UsedProbes { get; private set; }

    /// <summary>Whether the last frame this feature ran in could trace reflections.</summary>
    public bool UsedReflections { get; private set; }

    public void Resize(uint width, uint height)
    {
        _ctx.Targets.Ensure(SdfTargets.Hdr, _ctx.FrameTarget(SdfTargets.HdrFormat));
        _ctx.Targets.Ensure(SdfTargets.Depth, _ctx.FrameTarget(SdfTargets.DepthFormat));
    }

    public void OnEnabledChanged(bool enabled)
    {
        ShapeCount = 0;
        LightCount = 0;
        UsedProbes = false;
        UsedReflections = false;
    }

    public void Setup(in FrameContext frame)
    {
        var data = _ctx.SceneData;
        data.Upload(_ctx);
        ShapeCount = data.ShapeCount;
        LightCount = data.LightCount;
        UsedProbes = frame.Blackboard.TryGet(SdfResults.Probes, out var probes);
        UsedReflections = _ctx.Scene.Reflections.Enabled;
        var index = (UsedProbes ? 2 : 0) | (UsedReflections ? 1 : 0);
        var variant = Ensure(index);

        var graph = frame.Graph;
        var hdr = graph.Texture(SdfTargets.Hdr);
        var pass = graph.AddRasterPass("Sdf.Scene", RenderPassEvent.Opaque)
            .Color(0, hdr, LoadOp.Clear, clear: new ColorRgba(0f, 0f, 0f, 1f))
            .Depth(graph.Texture(SdfTargets.Depth), LoadOp.Clear, clear: 1f);
        var frameUniforms = GraphBinding.Buffer(0, data.FrameBuffer, 0, SdfSceneData.FrameBytes);
        var shapes = GraphBinding.Buffer(1, data.ShapeBuffer, 0, SdfSceneData.ShapeBytes);
        if (UsedProbes)
        {
            pass.BindGroup(0, "SdfSceneProbes", variant.Layout,
            [
                frameUniforms, shapes,
                GraphBinding.Texture(2, probes),
                GraphBinding.Buffer(3, _ctx.GiUniforms, 0, SdfContext.GiBytes),
            ]);
        }
        else
        {
            pass.BindGroup(0, "SdfScene", variant.Layout, [frameUniforms, shapes]);
        }
        pass.Record(this, Record, index);
        frame.Blackboard.Publish(SdfResults.SceneColor, hdr);
    }

    private Variant Ensure(int index)
    {
        if (_variants[index] is { } existing) return existing;
        var program = SdfContext.LoadProgram(s_programs[index]);
        SdfUniformLayout.ValidateScene(program, probes: (index & 2) != 0);
        // Every fragment writes its own depth, including 1 for the sky, so nothing may be rejected.
        var pipeline = _ctx.Renderer.CreatePipeline(program, SdfTargets.HdrFormat,
            depthStencilFormat: SdfTargets.DepthFormat, depthWriteEnabled: true, depthCompare: CompareFunction.Always);
        return _variants[index] = new Variant(pipeline, SdfContext.FindGroup(program, 0));
    }

    private static void Record(SdfSceneFeature self, ref PassRecording pass, int index) =>
        SdfContext.DrawFullscreen(ref pass, self._variants[index]!.Pipeline);

    public void Dispose()
    {
        foreach (var variant in _variants)
            if (variant is not null) _ctx.Renderer.DestroyPipeline(variant.Pipeline);
    }

    private sealed record Variant(PipelineHandle Pipeline, BindGroupLayoutDesc Layout);
}
