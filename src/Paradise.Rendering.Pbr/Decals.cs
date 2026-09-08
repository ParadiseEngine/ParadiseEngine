using System.Numerics;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Pbr;

/// <summary>An immutable, top-to-bottom RGBA8 decal image, copied from caller-owned pixels.</summary>
/// <remarks>Color images contain sRGB RGB and linear alpha; normal and metallic/roughness images contain linear data.</remarks>
public sealed class PbrDecalTexture
{
    private readonly byte[] _pixels;

    public PbrDecalTexture(int width, int height, ReadOnlySpan<byte> rgba)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 4096);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, 4096);
        if (rgba.Length != checked(width * height * 4))
            throw new ArgumentException("Expected exactly width × height × 4 RGBA bytes.", nameof(rgba));
        Width = width;
        Height = height;
        _pixels = rgba.ToArray();
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlySpan<byte> Pixels => _pixels;
}

/// <summary>The immutable surface painted by a decal before PBR lighting.</summary>
/// <remarks>Factors are linear. Coverage is color texture alpha × Color.W × decal opacity, shared by all channels.</remarks>
public sealed record PbrDecalMaterial
{
    public PbrDecalTexture? ColorTexture { get; init; }
    /// <summary>Tangent-space normal RGB, with +X right and +Y up in the projector's local XY plane.</summary>
    public PbrDecalTexture? NormalTexture { get; init; }
    /// <summary>Linear glTF packing: G roughness and B metallic; R and A are unused.</summary>
    public PbrDecalTexture? MetallicRoughnessTexture { get; init; }
    public Vector4 Color { get; init; } = Vector4.One;
    public Vector3 Emission { get; init; }
    public float Roughness { get; init; } = 1f;
    public float Metallic { get; init; }
    public float NormalStrength { get; init; } = 1f;
    public float ColorWeight { get; init; } = 1f;
    public float NormalWeight { get; init; } = 1f;
    public float MaterialWeight { get; init; }
    public float EmissionWeight { get; init; }
}

/// <summary>A local unit cube centered at zero, projected along local −Z onto facing surfaces.</summary>
/// <remarks>Model supports invertible affine transforms, including nonuniform scale, reflection and shear.
/// UV is (local.x + 0.5, 0.5 − local.y). Greater Order paints later; equal orders preserve list order.</remarks>
public sealed class PbrDecal
{
    public required PbrDecalMaterial Material { get; init; }
    public Matrix4x4 Model = Matrix4x4.Identity;
    public bool Enabled = true;
    public int Order;
    public float Opacity = 1f;
    public float AngleFadeStartDegrees = 60f;
    public float AngleFadeEndDegrees = 80f;
    /// <summary>Fade width in local cube units at each XY boundary, in [0, 0.5].</summary>
    public float EdgeFade = 0.02f;
    /// <summary>Fade width in local cube units at each Z boundary, in [0, 0.5].</summary>
    public float DepthFade = 0.02f;

    /// <summary>Tests volume clipping and geometric facing, returning UV and coverage before texture alpha.</summary>
    public bool TryProject(Vector3 worldPosition, Vector3 worldNormal, out Vector2 uv, out float coverage)
    {
        uv = default;
        coverage = 0f;
        if (!Enabled || !DecalPacking.TryPack(this, 0, out var gpu)) return false;
        var local = Vector3.Transform(worldPosition, gpu.WorldToLocal);
        if (!DecalPacking.Finite(local) || MathF.Abs(local.X) > 0.5f || MathF.Abs(local.Y) > 0.5f || MathF.Abs(local.Z) > 0.5f)
            return false;
        var normalLength = worldNormal.Length();
        if (!float.IsFinite(normalLength) || normalLength < 1e-10f) return false;
        var facing = Vector3.Dot(worldNormal / normalLength, new Vector3(gpu.ProjectionNormal.X, gpu.ProjectionNormal.Y, gpu.ProjectionNormal.Z));
        var angle = Math.Clamp((facing - gpu.Projection.Y) / MathF.Max(gpu.Projection.X - gpu.Projection.Y, 1e-5f), 0f, 1f);
        var edge = gpu.Projection.Z > 0f ? Math.Clamp((0.5f - MathF.Max(MathF.Abs(local.X), MathF.Abs(local.Y))) / gpu.Projection.Z, 0f, 1f) : 1f;
        var depth = gpu.Projection.W > 0f ? Math.Clamp((0.5f - MathF.Abs(local.Z)) / gpu.Projection.W, 0f, 1f) : 1f;
        uv = new Vector2(local.X + 0.5f, 0.5f - local.Y);
        coverage = angle * edge * depth * gpu.Factors.W * gpu.Color.W;
        return coverage > 0f;
    }
}

/// <summary>Scene decal settings and ordered volumes, snapshotted when a frame starts.</summary>
public sealed class PbrDecals
{
    public const int MaxDecals = 32;
    public bool Enabled = true;
    /// <summary>Maximum array dimension, a power of two from 1 to 512; larger source maps are downsampled in linear space.</summary>
    public int TextureSize = 256;
    public List<PbrDecal> Volumes { get; } = [];
}

// Mirrored in Common/decals.slang; float4 lanes keep storage-buffer layout identical on all backends.
[StructLayout(LayoutKind.Sequential, Size = 192)]
internal struct DecalGpu
{
    public Matrix4x4 WorldToLocal;
    public Vector4 ProjectionNormal;
    public Vector4 Tangent;
    public Vector4 Color;
    public Vector4 Emission;
    public Vector4 Factors;
    public Vector4 Weights;
    public Vector4 Projection;
    public Vector4 Atlas;
}

internal static class DecalPacking
{
    internal static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Vector4 value) => Finite(new Vector3(value.X, value.Y, value.Z)) && float.IsFinite(value.W);
    private static bool Finite(Matrix4x4 value) =>
        Finite(new Vector4(value.M11, value.M12, value.M13, value.M14)) &&
        Finite(new Vector4(value.M21, value.M22, value.M23, value.M24)) &&
        Finite(new Vector4(value.M31, value.M32, value.M33, value.M34)) &&
        Finite(new Vector4(value.M41, value.M42, value.M43, value.M44));

    internal static bool TryPack(PbrDecal decal, int atlasLayer, out DecalGpu gpu)
    {
        gpu = default;
        var model = decal.Model;
        if (!Finite(model) || model.M14 != 0f || model.M24 != 0f || model.M34 != 0f || model.M44 != 1f ||
            !Matrix4x4.Invert(model, out var inverse) || !Finite(inverse)) return false;
        var normal = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, Matrix4x4.Transpose(inverse)));
        var tangent = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, model));
        if (!Finite(normal) || !Finite(tangent)) return false;
        var material = decal.Material;
        ArgumentNullException.ThrowIfNull(material);
        if (!Finite(material.Color) || !Finite(material.Emission))
            throw new ArgumentException("Decal color and emission factors must be finite.", nameof(decal));
        var start = Clamp(decal.AngleFadeStartDegrees, 0f, 89.9f);
        var end = Clamp(decal.AngleFadeEndDegrees, start + 0.01f, 90f);
        var handedness = Vector3.Dot(Vector3.Cross(normal, tangent), Vector3.TransformNormal(Vector3.UnitY, model)) < 0f ? -1f : 1f;
        gpu = new DecalGpu
        {
            WorldToLocal = inverse,
            ProjectionNormal = new Vector4(normal, 0f),
            Tangent = new Vector4(tangent, handedness),
            Color = new Vector4(Vector3.Max(new Vector3(material.Color.X, material.Color.Y, material.Color.Z), Vector3.Zero), Clamp(material.Color.W)),
            Emission = new Vector4(Vector3.Max(material.Emission, Vector3.Zero), 0f),
            Factors = new Vector4(Clamp(material.Roughness), Clamp(material.Metallic), Clamp(material.NormalStrength, 0f, 4f), Clamp(decal.Opacity)),
            Weights = new Vector4(Clamp(material.ColorWeight), material.NormalTexture is null ? 0f : Clamp(material.NormalWeight), Clamp(material.MaterialWeight), Clamp(material.EmissionWeight)),
            Projection = new Vector4(MathF.Cos(start * MathF.PI / 180f), MathF.Cos(end * MathF.PI / 180f), Clamp(decal.EdgeFade, 0f, 0.5f), Clamp(decal.DepthFade, 0f, 0.5f)),
            Atlas = new Vector4(atlasLayer, 0f, 0f, 0f),
        };
        return true;
    }

    private static float Clamp(float value, float min = 0f, float max = 1f)
    {
        if (!float.IsFinite(value)) throw new ArgumentException("Decal factors must be finite.");
        return Math.Clamp(value, min, max);
    }
}
