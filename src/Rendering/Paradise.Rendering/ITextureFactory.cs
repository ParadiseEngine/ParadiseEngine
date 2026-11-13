using System;

namespace Paradise.Rendering;

/// <summary>The texture half of <see cref="IRenderer"/>: creation, upload and destruction of
/// textures and views. Split out so a component that only owns textures — the frame graph's
/// target registry — can be driven by a fake in a test that has no GPU, the same reason the
/// renderer's capture queue was extracted from its native calls.</summary>
public interface ITextureFactory
{
    /// <summary>Create a texture. Requesting a format whose
    /// <see cref="TextureFormats.RequiredCompression"/> is outside
    /// <see cref="IRenderer.SupportedTextureCompression"/> throws.</summary>
    TextureHandle CreateTexture(in TextureDesc desc);

    /// <summary>Upload one mip level. <paramref name="bytesPerRow"/> is the source row pitch in
    /// bytes (for block-compressed formats: bytes per row of 4-texel blocks); <paramref name="rowsPerImage"/>
    /// the number of rows (block rows when compressed); <paramref name="width"/>/<paramref name="height"/>
    /// the copy extent, which for block-compressed formats is the mip's texel size rounded up to
    /// whole blocks. Array layers are consecutive images in the payload, starting at layer zero.
    /// Block-size math stays in the asset layer.</summary>
    void WriteTexture(TextureHandle handle, uint mipLevel, ReadOnlySpan<byte> data, uint bytesPerRow, uint rowsPerImage, uint width, uint height, uint depthOrArrayLayers = 1);

    /// <summary>Upload a <paramref name="width"/> x <paramref name="height"/> rectangle of one 2D mip
    /// level with its top-left texel at (<paramref name="originX"/>, <paramref name="originY"/>): the
    /// partial update a glyph atlas makes when it rasterizes new characters. Uncompressed formats only;
    /// <paramref name="bytesPerRow"/> is the source row pitch.</summary>
    /// <remarks>The default serves a rectangle at the origin through <see cref="WriteTexture"/>, so a
    /// factory without region uploads (a test fake) still takes whole-texture writes; the GPU backends
    /// implement every origin.</remarks>
    /// <exception cref="NotSupportedException">The origin is not zero and this factory cannot place it.</exception>
    void WriteTextureRegion(TextureHandle handle, uint mipLevel, uint originX, uint originY, ReadOnlySpan<byte> data, uint bytesPerRow, uint width, uint height)
    {
        if (originX != 0 || originY != 0)
            throw new NotSupportedException($"{GetType().Name} cannot upload a texture region away from the origin.");
        WriteTexture(handle, mipLevel, data, bytesPerRow, height, width, height);
    }

    /// <summary>Destroy a texture. Views created from it must be destroyed separately.</summary>
    void DestroyTexture(TextureHandle handle);

    /// <summary>Create an explicit view into a texture (a chosen dimension / array-layer range) —
    /// e.g. a single layer of the shadow-map array as a render target, or the whole array as a
    /// D2Array sampling view.</summary>
    TextureViewHandle CreateTextureView(in TextureViewDesc desc);

    /// <summary>Destroy a texture view.</summary>
    void DestroyTextureView(TextureViewHandle handle);
}
