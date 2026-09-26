namespace Paradise.Rendering;

/// <summary>Static helpers for the <see cref="TextureFormat"/> enum.</summary>
public static class TextureFormats
{
    /// <summary>The compression family a device must grant before creating a texture in
    /// <paramref name="format"/>; <see cref="TextureCompressionFormats.None"/> for uncompressed formats.</summary>
    public static TextureCompressionFormats RequiredCompression(TextureFormat format) => format switch
    {
        >= TextureFormat.Bc1RgbaUnorm and <= TextureFormat.Bc7RgbaUnormSrgb => TextureCompressionFormats.Bc,
        TextureFormat.Etc2Rgba8Unorm or TextureFormat.Etc2Rgba8UnormSrgb or TextureFormat.EacRg11Unorm => TextureCompressionFormats.Etc2,
        TextureFormat.Astc4x4Unorm or TextureFormat.Astc4x4UnormSrgb => TextureCompressionFormats.Astc,
        _ => TextureCompressionFormats.None,
    };
}
