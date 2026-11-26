using System.Numerics;

namespace Paradise.Rendering.Pbr;

/// <summary>Material blending and coverage classification for PBR draw preparation.</summary>
public enum PbrAlphaMode : byte
{
    Opaque,
    // Conservatively excludes cutout materials from opaque occlusion and reordering;
    // fragment discard itself belongs to a custom material program.
    Mask,
    Blend,
}

/// <summary>Offset, scale and rotation of the material's base-color texture coordinates.</summary>
public readonly record struct PbrUvTransform(Vector2 Offset, Vector2 Scale, float Rotation)
{
    public static readonly PbrUvTransform Identity = new(Vector2.Zero, Vector2.One, 0f);
}

/// <summary>Renderer-owned material parameters, independent of source containers and asset documents.</summary>
/// <remarks>Loaders map cooked material documents to this description and resolve texture dependencies
/// separately through PbrMaterialTextures. Custom programs implement fragment discard for masked
/// materials.</remarks>
public sealed record PbrMaterialDesc
{
    public string? Name { get; init; }

    /// <summary>Whether both sides of a triangle render. False culls back faces (counter-clockwise
    /// winding is front) in the scene, prepass, motion-vector and occluder passes.</summary>
    /// <remarks>True draws both sides and shades a back face with its normal flipped, as glTF
    /// specifies for <c>doubleSided</c>. It defaults to true, which keeps a description that does not
    /// set it rendering as before; glTF's own default is false, so a loader maps the document's
    /// value. Shadow casters render both sides either way, so a single-sided wall still blocks light
    /// from behind.</remarks>
    public bool DoubleSided { get; init; } = true;
    public Vector4 BaseColorFactor { get; init; } = Vector4.One;
    public float MetallicFactor { get; init; }
    public float RoughnessFactor { get; init; } = 0.8f;
    public Vector3 EmissiveFactor { get; init; }
    public float NormalScale { get; init; } = 1f;
    public float OcclusionStrength { get; init; } = 1f;
    public float TransmissionFactor { get; init; }
    public PbrAlphaMode AlphaMode { get; init; }
    public PbrUvTransform BaseColorUvTransform { get; init; } = PbrUvTransform.Identity;
    public int ProcKind { get; init; }
    public float ProcNoiseScale { get; init; } = 1f;
    public float ProcFlowSpeed { get; init; } = 1f;
    public float ProcEmissiveStrength { get; init; } = 1f;
    public Vector3 ProcColorA { get; init; }
    public Vector3 ProcColorB { get; init; }
}

/// <summary>Independently resolved, upload-ready payloads for the five standard material texture slots.</summary>
/// <remarks>Null, or an empty payload, selects the slot's default texture. Each payload must be in a
/// format this renderer's device accepts (<see cref="IRenderer.SupportedTextureCompression"/>) and in
/// its slot's colour space: sRGB for base color and emissive, a normal-map layout for normal, linear
/// data otherwise. AddMaterial uploads the payloads synchronously and retains none of them. Uploaded
/// textures are shared by format and content until their last material is released.
/// <para>The renderer reads no texture file format. Asset paths, identities, I/O and decoding belong
/// to the runtime loader, which decodes off the render thread.</para></remarks>
public readonly record struct PbrMaterialTextures
{
    public CompressedTextureData? BaseColor { get; init; }
    public CompressedTextureData? MetallicRoughness { get; init; }
    public CompressedTextureData? Normal { get; init; }
    public CompressedTextureData? Occlusion { get; init; }
    public CompressedTextureData? Emissive { get; init; }
}
