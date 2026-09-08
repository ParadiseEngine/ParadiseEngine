using System.Numerics;
using Paradise.Rendering.Graph;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

public class PostProcessingGpuTests
{
    private const uint Size = 64;

    private static WebGpuRenderer? Backend(uint size = Size)
    {
        try { return WebGpuRenderer.CreateHeadless(size, size); }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No WebGPU adapter available: {error.Message}");
            return null;
        }
    }

    private static PbrScene Flat(float luminance) => new()
    {
        Camera = new PbrCamera { View = Matrix4x4.Identity, Projection = Matrix4x4.Identity },
        ClearColor = new ColorRgba(luminance, luminance, luminance, 1f),
        Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Linear },
    };

    private static byte[] Render(WebGpuRenderer backend, PbrRenderer pbr, PbrScene scene)
    {
        pbr.RenderFrame(scene);
        return backend.ReadbackColor(out _, out _);
    }

    private static double Difference(byte[] a, byte[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Average();
    private static double Mean(byte[] pixels) => pixels.Where((_, i) => i % 4 != 3).Average(x => (double)x);

    private sealed class PatternFeature : IRenderFeature
    {
        private readonly WebGpuRenderer _backend;
        private readonly PipelineHandle _pipeline;
        public PatternFeature(WebGpuRenderer backend)
        {
            _backend = backend;
            _pipeline = backend.CreatePipeline(ShaderProgramLoader.Load(typeof(PostProcessingGpuTests).Assembly,
                "Shaders.postPatternFixture"), PbrTargets.HdrFormat);
        }
        public FeatureDefinition Definition { get; } = new("test.postPattern", true, "Deterministic post-process input.");
        public FrameRequirements Requires => FrameRequirements.None;
        public void Resize(uint width, uint height) { }
        public void Setup(in FrameContext frame)
        {
            frame.Textures.Ensure("PostTestPattern", PbrTargets.RenderTarget(frame.Width, frame.Height, PbrTargets.HdrFormat));
            var target = frame.Graph.Texture("PostTestPattern");
            frame.Graph.AddRasterPass("Test.PostPattern", RenderPassEvent.AfterTransparent, 90)
                .Color(0, target, LoadOp.Clear).Record(this, Record);
            frame.Blackboard.Advance(PbrResults.SceneColor,
                frame.Blackboard.GetOrDefault(PbrResults.SceneColor, default), target);
        }
        private static void Record(PatternFeature self, ref PassRecording pass, int _)
        {
            pass.Encoder.SetPipeline(self._pipeline);
            pass.Encoder.Draw(new DrawCommand(3, 1, 0, 0));
        }
        public void Dispose() => _backend.DestroyPipeline(_pipeline);
    }

    private static PbrScene Pattern(PbrRenderer pbr, WebGpuRenderer backend)
    {
        pbr.Pipeline.Add(new PatternFeature(backend), 710);
        var scene = Flat(0.1f);
        float[] vertices =
        [
            -0.9f, -0.9f, 0.5f, 0, 0, 1, 0, 0, 1, 0, 0, 1,
             0.9f, -0.9f, 0.5f, 0, 0, 1, 1, 0, 1, 0, 0, 1,
             0.9f,  0.9f, 0.5f, 0, 0, 1, 1, 1, 1, 0, 0, 1,
            -0.9f,  0.9f, 0.5f, 0, 0, 1, 0, 1, 1, 0, 0, 1,
        ];
        var material = pbr.Materials.AddDefaultMaterial(Vector4.One);
        scene.Instances.Add(new PbrInstance { Mesh = new PbrMesh([pbr.UploadPrimitive(vertices, [0, 1, 2, 0, 2, 3], material)]) });
        return scene;
    }

    [Test]
    public async Task Manual_and_automatic_exposure_change_pixels_and_reset_after_disable_and_cut()
    {
        using var backend = Backend(63);
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 63, 63);
        var scene = Flat(0.018f);
        var baseline = Render(backend, pbr, scene);
        scene.Exposure = new PbrExposure { Enabled = true, CompensationEv = 2f };
        var manual = Render(backend, pbr, scene);
        await Assert.That(Mean(manual)).IsGreaterThan(Mean(baseline) + 30);
        scene.Exposure = new PbrExposure { Enabled = true, Automatic = true };
        var metered = Render(backend, pbr, scene);
        await Assert.That(Mean(metered)).IsBetween(116d, 120d);
        scene.ClearColor = new ColorRgba(1.8f, 1.8f, 1.8f, 1f);
        scene.DeltaSeconds = 0.1f;
        var adapting = Render(backend, pbr, scene);
        await Assert.That(Mean(adapting)).IsGreaterThan(Mean(metered) + 10);
        scene.TemporalHistoryVersion++;
        var cut = Render(backend, pbr, scene);
        await Assert.That(Mean(cut)).IsBetween(116d, 120d);
        pbr.Switches.Set(PbrFeatures.Exposure.Id, false);
        scene.ClearColor = new ColorRgba(0.018f, 0.018f, 0.018f, 1f);
        await Assert.That(Difference(Render(backend, pbr, scene), baseline)).IsEqualTo(0d);
        pbr.Switches.Set(PbrFeatures.Exposure.Id, true);
        await Assert.That(Mean(Render(backend, pbr, scene))).IsBetween(116d, 120d);
        scene.Exposure = scene.Exposure with { MaxEv = 0f };
        scene.TemporalHistoryVersion++;
        await Assert.That(Difference(Render(backend, pbr, scene), baseline)).IsLessThan(1d);
    }

    [Test]
    [Arguments("ColorGrading")]
    [Arguments("LensDistortion")]
    [Arguments("ChromaticAberration")]
    [Arguments("Vignette")]
    [Arguments("FilmGrain")]
    [Arguments("Sharpening")]
    [Arguments("DepthOfField")]
    public async Task Each_effect_changes_pixels_and_switching_it_off_restores_the_exact_baseline(string effect)
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Pattern(pbr, backend);
        var baseline = Render(backend, pbr, scene);
        FeatureDefinition definition;
        switch (effect)
        {
            case "ColorGrading":
                scene.ColorGrading = new PbrColorGrading { Enabled = true, Temperature = 0.5f, Tint = -0.2f,
                    Contrast = 1.3f, Saturation = 0.4f, Lift = new Vector3(0.03f), Gamma = new Vector3(1.2f), Gain = new Vector3(1.1f) };
                definition = PbrFeatures.ColorGrading;
                break;
            case "LensDistortion":
                scene.LensDistortion = new PbrLensDistortion { Enabled = true, Strength = 0.2f };
                definition = PbrFeatures.LensDistortion;
                break;
            case "ChromaticAberration":
                scene.ChromaticAberration = new PbrChromaticAberration { Enabled = true, IntensityPixels = 3f };
                definition = PbrFeatures.ChromaticAberration;
                break;
            case "Vignette":
                scene.Vignette = new PbrVignette { Enabled = true, Intensity = 0.8f };
                definition = PbrFeatures.Vignette;
                break;
            case "FilmGrain":
                scene.FilmGrain = new PbrFilmGrain { Enabled = true, Intensity = 0.2f, Seed = 37 };
                definition = PbrFeatures.FilmGrain;
                break;
            case "Sharpening":
                scene.Sharpening = new PbrSharpening { Enabled = true, Strength = 1f };
                definition = PbrFeatures.Sharpening;
                break;
            default:
                scene.DepthOfField = new PbrDepthOfField { Enabled = true, FocusDistance = 4f, FNumber = 0.5f, FocalLengthMm = 150f };
                definition = PbrFeatures.DepthOfField;
                break;
        }
        var enabled = Render(backend, pbr, scene);
        await Assert.That(Difference(baseline, enabled)).IsGreaterThan(0.1d);
        await Assert.That(pbr.LastPassNames.Contains("Post." + effect)).IsTrue();
        pbr.Switches.Set(definition.Id, false);
        var disabled = Render(backend, pbr, scene);
        await Assert.That(Difference(baseline, disabled)).IsEqualTo(0d);
        await Assert.That(pbr.LastPassNames.Contains("Post." + effect)).IsFalse();
        pbr.Switches.Set(definition.Id, true);
        await Assert.That(Difference(enabled, Render(backend, pbr, scene))).IsEqualTo(0d);
    }

    [Test]
    public async Task Flattened_lut_uses_all_three_axes_and_identity_keeps_one_output_transfer()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Pattern(pbr, backend);
        var baseline = Render(backend, pbr, scene);
        scene.ColorGrading = new PbrColorGrading { Enabled = true, Lut = PbrColorLut.Identity(8) };
        var identity = Render(backend, pbr, scene);
        await Assert.That(Difference(baseline, identity)).IsLessThan(0.3d);
        var colors = PbrColorLut.Identity(8).Colors.ToArray();
        for (var i = 0; i < colors.Length; i++) colors[i] = new Vector3(colors[i].Z, colors[i].X, colors[i].Y);
        scene.ColorGrading = scene.ColorGrading with { Lut = new PbrColorLut(8, colors) };
        var graded = Render(backend, pbr, scene);
        var error = 0L;
        var bgra = backend.ColorFormat is TextureFormat.Bgra8Unorm or TextureFormat.Bgra8UnormSrgb;
        var r = bgra ? 2 : 0;
        var b = 2 - r;
        for (var i = 0; i < graded.Length; i += 4)
        {
            error += Math.Abs(graded[i + r] - baseline[i + b]);
            error += Math.Abs(graded[i + 1] - baseline[i + r]);
            error += Math.Abs(graded[i + b] - baseline[i + 1]);
        }
        await Assert.That((double)error / (Size * Size * 3)).IsLessThan(0.5d);
        scene.ColorGrading = scene.ColorGrading with { LutStrength = 0f };
        await Assert.That(Difference(baseline, Render(backend, pbr, scene))).IsLessThan(0.3d);
    }

    [Test]
    public async Task Per_object_motion_blur_requires_valid_motion_and_resets_on_a_camera_cut()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Pattern(pbr, backend);
        scene.MotionBlur = new PbrMotionBlur { Enabled = true, ShutterAngle = 360f };
        var first = Render(backend, pbr, scene);
        await Assert.That(Difference(first, Render(backend, pbr, scene))).IsEqualTo(0d);
        scene.Instances[0].Model = Matrix4x4.CreateTranslation(0.25f, 0f, 0f);
        var moving = Render(backend, pbr, scene);
        await Assert.That(Difference(first, moving)).IsGreaterThan(0.5d);
        scene.Instances[0].Model = Matrix4x4.CreateTranslation(0.5f, 0f, 0f);
        scene.TemporalHistoryVersion++;
        await Assert.That(Difference(first, Render(backend, pbr, scene))).IsEqualTo(0d);
        pbr.Switches.Set(PbrFeatures.MotionVectors.Id, false);
        await Assert.That(Difference(first, Render(backend, pbr, scene))).IsEqualTo(0d);
        await Assert.That(pbr.LastPassNames.Contains("Post.MotionBlur")).IsFalse();
    }

    [Test]
    public async Task Full_stack_composes_and_scene_disable_releases_all_contributions()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var scene = Pattern(pbr, backend);
        var baseline = Render(backend, pbr, scene);
        scene.Exposure = new PbrExposure { Enabled = true, Automatic = true };
        scene.DepthOfField = new PbrDepthOfField { Enabled = true };
        scene.MotionBlur = new PbrMotionBlur { Enabled = true };
        scene.Bloom = new PbrBloom { Enabled = true, Threshold = 0.1f };
        scene.ColorGrading = new PbrColorGrading { Enabled = true, Saturation = 0.7f, Lut = PbrColorLut.Identity(8) };
        scene.LensDistortion = new PbrLensDistortion { Enabled = true, Strength = 0.05f };
        scene.ChromaticAberration = new PbrChromaticAberration { Enabled = true };
        scene.Vignette = new PbrVignette { Enabled = true };
        scene.FilmGrain = new PbrFilmGrain { Enabled = true };
        scene.Sharpening = new PbrSharpening { Enabled = true };
        var output = Render(backend, pbr, scene);
        await Assert.That(Difference(baseline, output)).IsGreaterThan(1d);
        var passes = pbr.LastPassNames.ToArray();
        await Assert.That(Array.IndexOf(passes, "Exposure.Apply")).IsLessThan(Array.IndexOf(passes, "Bloom.Bright"));
        await Assert.That(Array.IndexOf(passes, "Composite")).IsLessThan(Array.IndexOf(passes, "Post.ColorGrading"));
        await Assert.That(passes[^1]).IsEqualTo("Presentation");
        scene.Exposure = new(); scene.DepthOfField = new(); scene.MotionBlur = new(); scene.Bloom = new();
        scene.ColorGrading = new(); scene.LensDistortion = new(); scene.ChromaticAberration = new();
        scene.Vignette = new(); scene.FilmGrain = new(); scene.Sharpening = new();
        await Assert.That(Difference(baseline, Render(backend, pbr, scene))).IsEqualTo(0d);
        await Assert.That(pbr.LastPassNames.Any(p => p.StartsWith("Post.", StringComparison.Ordinal))).IsFalse();
    }
}
