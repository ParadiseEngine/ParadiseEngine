using System.Numerics;

namespace Paradise.Rendering.Pbr;

/// <summary>Exposure applied to HDR color before depth of field, motion blur and bloom.</summary>
public sealed record PbrExposure
{
    public bool Enabled { get; init; }
    public bool Automatic { get; init; }
    public float CompensationEv { get; init; }
    public float MinEv { get; init; } = -10f;
    public float MaxEv { get; init; } = 10f;
    public float MiddleGray { get; init; } = 0.18f;
    public float BrightenSpeed { get; init; } = 2f;
    public float DarkenSpeed { get; init; } = 4f;
}

/// <summary>Thin-lens depth of field for opaque surfaces, with distances in metres.</summary>
public sealed record PbrDepthOfField
{
    public bool Enabled { get; init; }
    public float FocusDistance { get; init; } = 5f;
    public float FocalLengthMm { get; init; } = 50f;
    public float FNumber { get; init; } = 2.8f;
    public float SensorHeightMm { get; init; } = 24f;
    public float MaxRadiusPixels { get; init; } = 16f;
}

/// <summary>Camera and per-object blur over a fraction of the frame's motion vector.</summary>
public sealed record PbrMotionBlur
{
    public bool Enabled { get; init; }
    public float ShutterAngle { get; init; } = 180f;
    public float MaxRadiusPixels { get; init; } = 32f;
    public int Samples { get; init; } = 16;
    public float DepthRejectionMetres { get; init; } = 0.1f;
}

/// <summary>Linear display-referred grading, optionally followed by a trilinear color lookup.</summary>
public sealed record PbrColorGrading
{
    public bool Enabled { get; init; }
    /// <summary>White-balance shift from neutral, in the range −1 (cool) to 1 (warm).</summary>
    public float Temperature { get; init; }
    public float Tint { get; init; }
    public float Contrast { get; init; } = 1f;
    public float Saturation { get; init; } = 1f;
    public Vector3 Lift { get; init; }
    public Vector3 Gamma { get; init; } = Vector3.One;
    public Vector3 Gain { get; init; } = Vector3.One;
    public PbrColorLut? Lut { get; init; }
    public float LutStrength { get; init; } = 1f;
}

/// <summary>An immutable linear RGB cube flattened as x = red + blue × size, y = green.</summary>
public sealed class PbrColorLut
{
    private readonly Vector3[] _colors;

    public PbrColorLut(int size, ReadOnlySpan<Vector3> colors)
    {
        if (size is < 2 or > 64) throw new ArgumentOutOfRangeException(nameof(size), "LUT size must be 2–64.");
        if (colors.Length != size * size * size) throw new ArgumentException("A LUT needs size cubed colors.", nameof(colors));
        foreach (var color in colors)
        {
            if (!float.IsFinite(color.X) || !float.IsFinite(color.Y) || !float.IsFinite(color.Z)
                || color.X < 0f || color.Y < 0f || color.Z < 0f
                || color.X > 1f || color.Y > 1f || color.Z > 1f)
                throw new ArgumentException("LUT colors must be finite linear RGB values in [0, 1].", nameof(colors));
        }
        Size = size;
        _colors = colors.ToArray();
    }

    public int Size { get; }
    public ReadOnlySpan<Vector3> Colors => _colors;

    public static PbrColorLut Identity(int size)
    {
        if (size is < 2 or > 64) throw new ArgumentOutOfRangeException(nameof(size));
        var colors = new Vector3[size * size * size];
        for (var green = 0; green < size; green++)
        for (var blue = 0; blue < size; blue++)
        for (var red = 0; red < size; red++)
            colors[green * size * size + blue * size + red] = new Vector3(red, green, blue) / (size - 1f);
        return new PbrColorLut(size, colors);
    }
}

/// <summary>Radial barrel or pincushion distortion in normalized screen coordinates.</summary>
public sealed record PbrLensDistortion
{
    public bool Enabled { get; init; }
    public float Strength { get; init; }
    public float Cubic { get; init; }
    public float Scale { get; init; } = 1f;
}

/// <summary>Radial red/blue channel separation, measured in pixels at the image edges.</summary>
public sealed record PbrChromaticAberration
{
    public bool Enabled { get; init; }
    public float IntensityPixels { get; init; } = 1f;
}

/// <summary>Elliptical vignette with normalized radius and a soft transition.</summary>
public sealed record PbrVignette
{
    public bool Enabled { get; init; }
    public float Intensity { get; init; } = 0.35f;
    public float Radius { get; init; } = 0.65f;
    public float Softness { get; init; } = 0.4f;
    public Vector3 Color { get; init; }
}

/// <summary>Zero-mean luminance-dependent grain, deterministic for a fixed elapsed time and seed.</summary>
public sealed record PbrFilmGrain
{
    public bool Enabled { get; init; }
    public float Intensity { get; init; } = 0.03f;
    public float Response { get; init; } = 0.8f;
    public uint Seed { get; init; }
}

/// <summary>Local contrast sharpening with neighborhood bounds to limit ringing.</summary>
public sealed record PbrSharpening
{
    public bool Enabled { get; init; }
    public float Strength { get; init; } = 0.3f;
}

/// <summary>CPU counterparts of exposure adaptation and thin-lens circle-of-confusion equations.</summary>
public static class PbrPostMath
{
    internal static float Finite(float value, float fallback, float minimum, float maximum) =>
        Math.Clamp(float.IsFinite(value) ? value : fallback, minimum, maximum);

    public static float AdaptExposureEv(float previous, float target, float speed, float deltaSeconds)
    {
        previous = Finite(previous, 0f, -24f, 24f);
        target = Finite(target, 0f, -24f, 24f);
        speed = Finite(speed, 0f, 0f, 100f);
        deltaSeconds = Finite(deltaSeconds, 0f, 0f, 1f);
        return previous + (target - previous) * (1f - MathF.Exp(-speed * deltaSeconds));
    }

    public static float CircleOfConfusionPixels(float depth, uint height, PbrDepthOfField settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var focal = Finite(settings.FocalLengthMm, 50f, 1f, 300f) * 0.001f;
        var focus = Finite(settings.FocusDistance, 5f, focal + 0.001f, 100000f);
        var fNumber = Finite(settings.FNumber, 2.8f, 0.5f, 64f);
        var sensor = Finite(settings.SensorHeightMm, 24f, 1f, 100f) * 0.001f;
        var radius = focal * focal / (2f * fNumber * (focus - focal) * sensor) * height;
        var maximum = Finite(settings.MaxRadiusPixels, 16f, 0f, 32f);
        return Math.Clamp(radius * (1f - focus / MathF.Max(depth, 0.001f)), -maximum, maximum);
    }
}
