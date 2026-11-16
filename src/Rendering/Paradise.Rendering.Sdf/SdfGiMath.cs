using System.Numerics;

namespace Paradise.Rendering.Sdf;

/// <summary>The lattice of irradiance probes fitted to a scene's <see cref="SdfGi"/> settings.</summary>
/// <param name="Origin">World position of the probe at (0, 0, 0).</param>
/// <param name="Spacing">Distance between neighbors along every axis.</param>
/// <param name="CountX">Probes along x.</param>
/// <param name="CountY">Probes along y.</param>
/// <param name="CountZ">Probes along z.</param>
public readonly record struct SdfProbeGrid(Vector3 Origin, float Spacing, int CountX, int CountY, int CountZ)
{
    public int Count => CountX * CountY * CountZ;

    /// <summary>Probes in one row of the atlas.</summary>
    public int PerRow => Math.Min(Count, SdfGiMath.ProbesPerRow);

    public uint AtlasWidth => (uint)(PerRow * SdfGiMath.TexelsPerProbe);

    public uint AtlasHeight => (uint)((Count + PerRow - 1) / PerRow);

    /// <summary>Probes are numbered along x, then z, then y: a horizontal layer at a time.</summary>
    public int Index(int x, int y, int z) => x + CountX * (z + CountZ * y);

    public Vector3 Position(int index) => Origin + Spacing * new Vector3(
        index % CountX, index / (CountX * CountZ), index / CountX % CountZ);
}

/// <summary>The CPU side of the probe passes: the grid fit and the ray set, as the shaders compute them.</summary>
public static class SdfGiMath
{
    /// <summary>The most probes a grid holds: one row of the ray texture each.</summary>
    public const int MaxProbes = 4096;

    public const int MaxRaysPerProbe = 64;

    /// <summary>Atlas texels a probe owns: mean radiance, its moment along x, y and z, validity.</summary>
    public const int TexelsPerProbe = 5;

    public const int ProbesPerRow = 64;

    /// <summary>Fits a grid over the settings' bounds: centered on them, covering them, at the
    /// requested spacing or the smallest larger one that keeps within the probe budget.</summary>
    public static SdfProbeGrid Fit(SdfGi settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var min = Vector3.Min(settings.BoundsMin, settings.BoundsMax);
        var max = Vector3.Max(settings.BoundsMin, settings.BoundsMax);
        if (!float.IsFinite(min.LengthSquared()) || !float.IsFinite(max.LengthSquared()))
        {
            min = -Vector3.One;
            max = Vector3.One;
        }
        var extent = max - min;
        var spacing = float.IsFinite(settings.ProbeSpacing) ? MathF.Max(settings.ProbeSpacing, 0.01f) : 1f;
        var budget = Math.Clamp(settings.MaxProbes, 8, MaxProbes);

        int x, y, z;
        while (true)
        {
            x = Along(extent.X, spacing);
            y = Along(extent.Y, spacing);
            z = Along(extent.Z, spacing);
            if ((long)x * y * z <= budget) break;
            spacing *= 1.05f;
        }
        var center = (min + max) * 0.5f;
        var origin = center - 0.5f * spacing * new Vector3(x - 1, y - 1, z - 1);
        return new SdfProbeGrid(origin, spacing, x, y, z);
    }

    // Enough cells to span the extent, so at least two probes.
    private static int Along(float extent, float spacing)
    {
        var cells = (int)Math.Min(MathF.Ceiling(extent / spacing - 1e-4f), MaxProbes);
        return Math.Max(cells, 1) + 1;
    }

    /// <summary>One direction of a spherical Fibonacci set, an even cover of the sphere, turned by
    /// <paramref name="rotation"/>. Twin of <c>sdfGiRayDirection</c>.</summary>
    public static Vector3 RayDirection(int ray, int count, in Matrix4x4 rotation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegative(ray);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ray, count);
        var z = 1f - (2f * ray + 1f) / count;
        var radius = MathF.Sqrt(MathF.Max(1f - z * z, 0f));
        var phi = 2.39996323f * ray; // the golden angle
        return Vector3.Normalize(Vector3.TransformNormal(new Vector3(radius * MathF.Cos(phi), radius * MathF.Sin(phi), z), rotation));
    }

    /// <summary>The rotation of the ray set in frame <paramref name="frame"/>: a low-discrepancy
    /// walk over all rotations, so successive frames sample between each other's rays.</summary>
    public static Matrix4x4 FrameRotation(int frame)
    {
        // The R3 sequence drives Shoemake's uniform quaternion.
        var u = Fraction(frame * 0.8191725133961645);
        var v = Fraction(frame * 0.6710436067037893) * MathF.Tau;
        var w = Fraction(frame * 0.5497004779019703) * MathF.Tau;
        var a = MathF.Sqrt(1f - u);
        var b = MathF.Sqrt(u);
        return Matrix4x4.CreateFromQuaternion(new Quaternion(a * MathF.Sin(v), a * MathF.Cos(v), b * MathF.Sin(w), b * MathF.Cos(w)));
    }

    private static float Fraction(double value) => (float)(value - Math.Floor(value));
}
