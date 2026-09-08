using System;
using System.Collections.Generic;

namespace Paradise.Rendering.Test;

/// <summary>Hands out unique handles and remembers which are alive, so a test can say what a
/// registry created, destroyed, or left dangling — the whole of what a registry is for.</summary>
internal sealed class FakeTextureFactory : ITextureFactory
{
    private uint _next = 1;

    public Dictionary<TextureHandle, TextureDesc> Textures { get; } = [];
    public Dictionary<TextureViewHandle, TextureViewDesc> Views { get; } = [];
    public int TexturesCreated { get; private set; }
    public int ViewsCreated { get; private set; }

    public TextureHandle CreateTexture(in TextureDesc desc)
    {
        var handle = new TextureHandle(_next++, 1);
        Textures.Add(handle, desc);
        TexturesCreated++;
        return handle;
    }

    public TextureViewHandle CreateTextureView(in TextureViewDesc desc)
    {
        if (!Textures.ContainsKey(desc.Texture))
            throw new InvalidOperationException("View over a texture that does not exist.");
        var handle = new TextureViewHandle(_next++, 1);
        Views.Add(handle, desc);
        ViewsCreated++;
        return handle;
    }

    public void DestroyTexture(TextureHandle handle)
    {
        if (!Textures.Remove(handle)) throw new InvalidOperationException("Texture destroyed twice.");
    }

    public void DestroyTextureView(TextureViewHandle handle)
    {
        if (!Views.Remove(handle)) throw new InvalidOperationException("View destroyed twice.");
    }

    public List<(TextureHandle Texture, int Bytes)> Writes { get; } = [];

    public void WriteTexture(TextureHandle handle, uint mipLevel, ReadOnlySpan<byte> data, uint bytesPerRow, uint rowsPerImage, uint width, uint height, uint depthOrArrayLayers = 1)
    {
        if (!Textures.ContainsKey(handle)) throw new InvalidOperationException("Write to a texture that does not exist.");
        Writes.Add((handle, data.Length));
    }
}
