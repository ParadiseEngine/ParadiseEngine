using System;

namespace Paradise.Rendering;

/// <summary>The texture half of <see cref="IRenderer"/>: creation, upload and destruction of
/// textures and views. Split out so a component that only owns textures — the frame graph's
/// target registry — can be driven by a fake in a test that has no GPU, the same reason the
/// renderer's capture queue was extracted from its native calls.</summary>
public interface ITextureFactory
{
    /// <summary>Create a texture. Requesting a <c>Bc*</c> format without
    /// <see cref="IRenderer.SupportsBcTextureCompression"/> throws.</summary>
    TextureHandle CreateTexture(in TextureDesc desc);

    /// <summary>Upload one mip level. <paramref name="bytesPerRow"/> is the source row pitch in
    /// bytes (for BC formats: bytes per row of 4-texel blocks); <paramref name="rowsPerImage"/>
    /// the number of rows (block rows for BC); <paramref name="width"/>/<paramref name="height"/>
    /// the mip's texel dimensions. Block-size math stays in the asset layer.</summary>
    void WriteTexture(TextureHandle handle, uint mipLevel, ReadOnlySpan<byte> data, uint bytesPerRow, uint rowsPerImage, uint width, uint height);

    /// <summary>Destroy a texture. Views created from it must be destroyed separately.</summary>
    void DestroyTexture(TextureHandle handle);

    /// <summary>Create an explicit view into a texture (a chosen dimension / array-layer range) —
    /// e.g. a single layer of the shadow-map array as a render target, or the whole array as a
    /// D2Array sampling view.</summary>
    TextureViewHandle CreateTextureView(in TextureViewDesc desc);

    /// <summary>Destroy a texture view.</summary>
    void DestroyTextureView(TextureViewHandle handle);
}
