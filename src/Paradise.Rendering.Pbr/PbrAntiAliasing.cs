using System.Numerics;

namespace Paradise.Rendering.Pbr;

/// <summary>Temporal accumulation of jittered HDR samples with motion and depth rejection.</summary>
public sealed record PbrTaa
{
    public bool Enabled { get; init; }
    /// <summary>Fraction of the reprojected history retained, limited to 0.98.</summary>
    public float HistoryWeight { get; init; } = 0.9f;
    /// <summary>Scale of the eight-sample Halton jitter in pixels; zero disables camera jitter.</summary>
    public float JitterScale { get; init; } = 1f;
    /// <summary>Relative view-depth tolerance for accepting a reprojected history sample.</summary>
    public float DepthThreshold { get; init; } = 0.02f;
    /// <summary>Standard deviations retained by the neighborhood variance clamp.</summary>
    public float VarianceGamma { get; init; } = 1.25f;
}

/// <summary>Edge-adaptive spatial antialiasing after tonemapping and display effects.</summary>
public sealed record PbrFxaa
{
    public bool Enabled { get; init; }
    public float EdgeThreshold { get; init; } = 0.125f;
    public float MinimumThreshold { get; init; } = 0.0312f;
    public float SubpixelQuality { get; init; } = 0.75f;
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
