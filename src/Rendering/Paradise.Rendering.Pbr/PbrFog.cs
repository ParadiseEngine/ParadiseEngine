using System.Numerics;

namespace Paradise.Rendering.Pbr;

/// <summary>Height fog with participating-medium scattering along each camera ray.</summary>
public sealed record PbrFog
{
    public bool Enabled { get; set; }
    public float Density { get; set; } = 0.02f;
    public float HeightFalloff { get; set; } = 0.1f;
    public float BaseHeight { get; set; }
    public Vector3 Color { get; set; } = new(0.4f, 0.5f, 0.6f);
    public float StartDistance { get; set; }
    public float MaxDistance { get; set; } = 100f;
    public int Steps { get; set; } = 32;
    public bool LightScattering { get; set; } = true;
    public float Anisotropy { get; set; } = 0.2f;
    public Vector3 Albedo { get; set; } = new(0.9f);
}

/// <summary>An additive local fog density in a transformed unit box, from -0.5 to +0.5 on each axis.</summary>
public sealed record PbrFogVolume
{
    public Matrix4x4 Transform { get; set; } = Matrix4x4.Identity;
    public float Density { get; set; } = 0.2f;
    public Vector3 Albedo { get; set; } = Vector3.One;
}

/// <summary>Reference equations for extinction and anisotropic volume scattering.</summary>
public static class FogMath
{
    public static float Transmittance(float density, float distance) =>
        MathF.Exp(-MathF.Max(density, 0f) * MathF.Max(distance, 0f));

    public static float HenyeyGreenstein(float cosine, float anisotropy)
    {
        var g = Math.Clamp(anisotropy, -0.95f, 0.95f);
        var denominator = 1f + g * g - 2f * g * Math.Clamp(cosine, -1f, 1f);
        return (1f - g * g) / (4f * MathF.PI * MathF.Pow(denominator, 1.5f));
    }
}
