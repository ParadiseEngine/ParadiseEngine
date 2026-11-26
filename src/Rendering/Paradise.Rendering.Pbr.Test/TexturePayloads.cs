namespace Paradise.Rendering.Pbr.Test;

/// <summary>Upload-ready texture payloads, as a loader hands them to the material cache.</summary>
internal static class TexturePayloads
{
    public static CompressedTextureData Pixel(TextureFormat format, byte r, byte g, byte b, byte a = 255) =>
        new([r, g, b, a], 1, 1, format, 1, 1, 4, [new CompressedTextureMipLevel(1, 1, 0, 4, 4, 1, 1, 1)]);

    /// <summary>The same bytes as <see cref="Data"/> in another format, so only the format tells them apart.</summary>
    public static CompressedTextureData Color() => Pixel(TextureFormat.Rgba8UnormSrgb, 200, 40, 20);

    public static CompressedTextureData Data() => Pixel(TextureFormat.Rgba8Unorm, 200, 40, 20);

    public static CompressedTextureData Normal() => Pixel(TextureFormat.Rgba8Unorm, 128, 128, 255);

    public static CompressedTextureData Empty() => new([], 0, 0, TextureFormat.Rgba8Unorm, 1, 1, 4, []);

    /// <summary>One color payload in both sRGB slots and one data payload in both linear slots: two textures.</summary>
    public static PbrMaterialTextures ColorAndData() => new()
    {
        BaseColor = Color(),
        MetallicRoughness = Data(),
        Occlusion = Data(),
        Emissive = Color(),
    };
}
