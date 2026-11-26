namespace Paradise.Assets.Textures;

/// <summary>What a texture is used for — selects the transcode target and its colour space:
/// color/emissive → sRGB; packed scalar maps (metallic-roughness/occlusion, glTF packs roughness
/// in G and metallic in B, so all four channels are kept) → linear; tangent-space normals →
/// a two-channel format (shader reconstructs Z = sqrt(1 − x² − y²)).</summary>
public enum CompressedTextureUsage
{
    ColorSrgb,
    LinearData,
    NormalMap,
}
