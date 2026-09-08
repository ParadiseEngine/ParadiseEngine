using System.Numerics;
using Paradise.Features;
using Paradise.Rendering.Graph;
using static Paradise.Rendering.Pbr.PbrPostMath;

namespace Paradise.Rendering.Pbr;

/// <summary>Thin-lens disk gathering with signed foreground and background circles of confusion.</summary>
public sealed class DepthOfFieldFeature : PostEffectFeature
{
    internal DepthOfFieldFeature(PbrContext ctx) : base(ctx, "DepthOfField", "depthOfFieldFragment", false, 1220) { }
    public override FeatureDefinition Definition => PbrFeatures.DepthOfField;
    private protected override bool SceneEnabled => Context.Scene.DepthOfField.Enabled;
    private protected override FrameRequirements Inputs => FrameRequirements.DepthNormalPrepass;
    private protected override PostUniformsGpu Parameters()
    {
        var s = Context.Scene.DepthOfField;
        var f = Finite(s.FocalLengthMm, 50f, 1f, 300f) * 0.001f;
        var focus = Finite(s.FocusDistance, 5f, f + 0.001f, 100000f);
        var aperture = Finite(s.FNumber, 2.8f, 0.5f, 64f);
        var sensor = Finite(s.SensorHeightMm, 24f, 1f, 100f) * 0.001f;
        return new PostUniformsGpu
        {
            A = new Vector4(focus, f * f / (2f * aperture * (focus - f) * sensor) * Context.Height,
                Finite(s.MaxRadiusPixels, 16f, 0f, 32f), 0f),
        };
    }
}

/// <summary>Motion-vector gathering with validity and foreground-depth rejection.</summary>
public sealed class MotionBlurFeature : PostEffectFeature
{
    internal MotionBlurFeature(PbrContext ctx) : base(ctx, "MotionBlur", "motionBlurFragment", false, 1230) { }
    public override FeatureDefinition Definition => PbrFeatures.MotionBlur;
    private protected override bool SceneEnabled => Context.Scene.MotionBlur.Enabled;
    private protected override FrameRequirements Inputs => FrameRequirements.DepthNormalPrepass | FrameRequirements.MotionVectors;
    private protected override PostUniformsGpu Parameters()
    {
        var s = Context.Scene.MotionBlur;
        return new PostUniformsGpu
        {
            A = new Vector4(Finite(s.ShutterAngle, 180f, 0f, 360f) / 360f,
                Finite(s.MaxRadiusPixels, 32f, 0f, 64f), Math.Clamp(s.Samples, 2, 32),
                Finite(s.DepthRejectionMetres, 0.1f, 0f, 100f)),
            B = new Vector4(Context.MotionJitterDelta, 0f, 0f),
        };
    }
}

/// <summary>White balance, contrast, saturation, lift/gamma/gain and flattened RGB LUT grading.</summary>
public sealed class ColorGradingFeature : PostEffectFeature
{
    internal ColorGradingFeature(PbrContext ctx) : base(ctx, "ColorGrading", "colorGradingFragment", true, 1610) { }
    public override FeatureDefinition Definition => PbrFeatures.ColorGrading;
    private protected override bool SceneEnabled => Context.Scene.ColorGrading.Enabled;
    private protected override PbrColorLut? ColorLut => Context.Scene.ColorGrading.Lut;
    private protected override PostUniformsGpu Parameters()
    {
        var s = Context.Scene.ColorGrading;
        return new PostUniformsGpu
        {
            A = new Vector4(Finite(s.Temperature, 0f, -1f, 1f), Finite(s.Tint, 0f, -1f, 1f),
                Finite(s.Contrast, 1f, 0f, 4f), Finite(s.Saturation, 1f, 0f, 4f)),
            B = new Vector4(Vector(s.Lift, 0f, -1f, 1f), 0f),
            C = new Vector4(Vector(s.Gamma, 1f, 0.01f, 8f), 0f),
            D = new Vector4(Vector(s.Gain, 1f, 0f, 8f), 0f),
            E = new Vector4(s.Lut?.Size ?? 0, Finite(s.LutStrength, 1f, 0f, 1f), 0f, 0f),
        };
    }

    private static Vector3 Vector(Vector3 value, float fallback, float minimum, float maximum) => new(
        Finite(value.X, fallback, minimum, maximum), Finite(value.Y, fallback, minimum, maximum),
        Finite(value.Z, fallback, minimum, maximum));
}

/// <summary>Radial polynomial lens distortion with transparent-edge-safe black borders.</summary>
public sealed class LensDistortionFeature : PostEffectFeature
{
    internal LensDistortionFeature(PbrContext ctx) : base(ctx, "LensDistortion", "lensDistortionFragment", true, 1620) { }
    public override FeatureDefinition Definition => PbrFeatures.LensDistortion;
    private protected override bool SceneEnabled => Context.Scene.LensDistortion.Enabled;
    private protected override PostUniformsGpu Parameters()
    {
        var s = Context.Scene.LensDistortion;
        return new PostUniformsGpu { A = new Vector4(Finite(s.Strength, 0f, -0.8f, 0.8f),
            Finite(s.Cubic, 0f, -0.5f, 0.5f), Finite(s.Scale, 1f, 0.25f, 4f), 0f) };
    }
}

/// <summary>Radial chromatic aberration separating red and blue around the green image.</summary>
public sealed class ChromaticAberrationFeature : PostEffectFeature
{
    internal ChromaticAberrationFeature(PbrContext ctx) : base(ctx, "ChromaticAberration", "chromaticAberrationFragment", true, 1625) { }
    public override FeatureDefinition Definition => PbrFeatures.ChromaticAberration;
    private protected override bool SceneEnabled => Context.Scene.ChromaticAberration.Enabled;
    private protected override PostUniformsGpu Parameters() => new()
    {
        A = new Vector4(Finite(Context.Scene.ChromaticAberration.IntensityPixels, 1f, 0f, 16f), 0f, 0f, 0f),
    };
}

/// <summary>Soft elliptical vignette in linear display color.</summary>
public sealed class VignetteFeature : PostEffectFeature
{
    internal VignetteFeature(PbrContext ctx) : base(ctx, "Vignette", "vignetteFragment", true, 1630) { }
    public override FeatureDefinition Definition => PbrFeatures.Vignette;
    private protected override bool SceneEnabled => Context.Scene.Vignette.Enabled;
    private protected override PostUniformsGpu Parameters()
    {
        var s = Context.Scene.Vignette;
        return new PostUniformsGpu
        {
            A = new Vector4(Finite(s.Intensity, 0.35f, 0f, 1f), Finite(s.Radius, 0.65f, 0f, 1.5f),
                Finite(s.Softness, 0.4f, 0.001f, 1.5f), 0f),
            B = new Vector4(Finite(s.Color.X, 0f, 0f, 1f), Finite(s.Color.Y, 0f, 0f, 1f), Finite(s.Color.Z, 0f, 0f, 1f), 0f),
        };
    }
}

/// <summary>Deterministic animated grain with luminance-dependent amplitude.</summary>
public sealed class FilmGrainFeature : PostEffectFeature
{
    internal FilmGrainFeature(PbrContext ctx) : base(ctx, "FilmGrain", "filmGrainFragment", true, 1640) { }
    public override FeatureDefinition Definition => PbrFeatures.FilmGrain;
    private protected override bool SceneEnabled => Context.Scene.FilmGrain.Enabled;
    private protected override PostUniformsGpu Parameters()
    {
        var s = Context.Scene.FilmGrain;
        return new PostUniformsGpu { A = new Vector4(Finite(s.Intensity, 0.03f, 0f, 0.5f),
            Finite(s.Response, 0.8f, 0f, 1f), MathF.Floor(Finite(Context.Scene.ElapsedSeconds, 0f, 0f, 10000000f) * 24f), BitConverter.UInt32BitsToSingle(s.Seed)) };
    }
}

/// <summary>Cross-neighborhood sharpening limited to the local color range.</summary>
public sealed class SharpeningFeature : PostEffectFeature
{
    internal SharpeningFeature(PbrContext ctx) : base(ctx, "Sharpening", "sharpeningFragment", true, 1650) { }
    public override FeatureDefinition Definition => PbrFeatures.Sharpening;
    private protected override bool SceneEnabled => Context.Scene.Sharpening.Enabled;
    private protected override PostUniformsGpu Parameters() => new()
    {
        A = new Vector4(Finite(Context.Scene.Sharpening.Strength, 0.3f, 0f, 1f), 0f, 0f, 0f),
    };
}
