using System.Numerics;

namespace Paradise.Rendering.Pbr;

/// <summary>Temporal accumulation of jittered HDR samples with motion and depth rejection.</summary>
public sealed record PbrTaa
{
    public bool Enabled { get; set; }
    /// <summary>Fraction of the reprojected history retained, limited to 0.98.</summary>
    public float HistoryWeight { get; set; } = 0.9f;
    /// <summary>Scale of the eight-sample Halton jitter in pixels; zero disables camera jitter.</summary>
    public float JitterScale { get; set; } = 1f;
    /// <summary>Relative view-depth tolerance for accepting a reprojected history sample.</summary>
    public float DepthThreshold { get; set; } = 0.02f;
    /// <summary>Standard deviations retained by the neighborhood variance clamp.</summary>
    public float VarianceGamma { get; set; } = 1.25f;
}

/// <summary>Edge-adaptive spatial antialiasing after tonemapping and display effects.</summary>
public sealed record PbrFxaa
{
    public bool Enabled { get; set; }
    public float EdgeThreshold { get; set; } = 0.125f;
    public float MinimumThreshold { get; set; } = 0.0312f;
    public float SubpixelQuality { get; set; } = 0.75f;
}

/// <summary>A value copy of <see cref="PbrTaa"/>, so history resets on a settings change whether the
/// record was replaced or edited in place.</summary>
internal readonly record struct TaaSettings(bool Enabled, float HistoryWeight, float JitterScale, float DepthThreshold, float VarianceGamma)
{
    public static TaaSettings Of(PbrTaa taa) =>
        new(taa.Enabled, taa.HistoryWeight, taa.JitterScale, taa.DepthThreshold, taa.VarianceGamma);
}

internal static class AntiAliasingMath
{
    public const uint SampleCount = 8;

    public static Vector2 Jitter(uint index) => new(Halton(index % SampleCount + 1, 2) - 0.5f,
        Halton(index % SampleCount + 1, 3) - 0.5f);

    private static float Halton(uint index, uint radix)
    {
        var result = 0f;
        var scale = 1f;
        while (index > 0)
        {
            scale /= radix;
            result += scale * (index % radix);
            index /= radix;
        }
        return result;
    }

    public static Matrix4x4 JitterProjection(in Matrix4x4 projection, Vector2 pixels, uint width, uint height)
    {
        // Row-vector post-multiplication offsets clip XY by W, for perspective and orthographic cameras.
        return projection * Matrix4x4.CreateTranslation(2f * pixels.X / Math.Max(1, width),
            -2f * pixels.Y / Math.Max(1, height), 0f);
    }

    public static float FiniteClamp(float value, float minimum, float maximum, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
