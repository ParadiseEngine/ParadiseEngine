using System;
using Hexa.NET.ImGui;

namespace Paradise.Ui.ImGui;

/// <summary>Copies and acknowledges ImGui 1.92 texture requests on the ImGui thread.</summary>
/// <remarks>Acknowledgements happen immediately to avoid touching ImTextureData from the render
/// thread. ImGuiTextureOps preserves every copied create, update and destroy operation until
/// applied.</remarks>
public static class ImGuiTextureCapture
{
    /// <summary>Captures and acknowledges texture requests after Render and before NewFrame on the
    /// ImGui thread.</summary>
    /// <exception cref="NotSupportedException">A texture arrived in a format other than RGBA32.
    /// Setting <c>io.Fonts.TexDesiredFormat = Alpha8</c> without teaching the renderer that
    /// format would otherwise upload garbage.</exception>
    public static unsafe void CaptureFrom(ImDrawDataPtr drawData, ImGuiTextureOps ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        if (drawData.IsNull) return;

        // ImDrawData.Textures is a POINTER to the vector (ImGui points it at the platform-IO
        // list), and it is null on a context that never rendered.
        var textures = drawData.Handle->Textures;
        if (textures is null) return;

        for (var i = 0; i < textures->Size; i++)
        {
            var texture = (*textures)[i];
            switch (texture.Status)
            {
                case ImTextureStatus.WantCreate:
                    ops.Enqueue(CreateOp(texture));
                    // Zero is ImGui's null ID; offset UniqueID by one, below the reserved
                    // host-texture range.
                    texture.SetTexID(new ImTextureID(TextureIdOf(texture)));
                    texture.SetStatus(ImTextureStatus.Ok);
                    break;

                case ImTextureStatus.WantUpdates:
                    ops.Enqueue(UpdateOp(texture));
                    texture.SetStatus(ImTextureStatus.Ok);
                    break;

                case ImTextureStatus.WantDestroy:
                    // A texture ImGui asked to destroy before it was ever created has no id and
                    // nothing was allocated for it — acknowledge it and enqueue nothing.
                    if (!texture.GetTexID().IsNull)
                    {
                        ops.Enqueue(ImGuiTextureOp.Destroy(texture.GetTexID().Handle));
                    }
                    // Clear the ID before reporting Destroyed; debug ImGui asserts this atlas
                    // contract.
                    texture.SetTexID(ImTextureID.Null);
                    texture.SetStatus(ImTextureStatus.Destroyed);
                    // Acknowledge immediately; the ordered renderer queue delays both resource
                    // destruction and lookup removal for in-flight snapshots.
                    break;

            }
        }
    }

    private static unsafe ImGuiTextureOp CreateOp(ImTextureDataPtr texture)
    {
        RequireRgba32(texture);
        var width = (uint)texture.Width;
        var height = (uint)texture.Height;
        // A freshly created ImTextureData is tightly packed (pitch == width * bpp), so the whole
        // buffer copies in one go.
        var pixels = new ReadOnlySpan<byte>(texture.GetPixels(), texture.GetSizeInBytes()).ToArray();
        return ImGuiTextureOp.Create(TextureIdOf(texture), width, height, pixels);
    }

    private static unsafe ImGuiTextureOp UpdateOp(ImTextureDataPtr texture)
    {
        RequireRgba32(texture);
        var rect = texture.UpdateRect;
        var rowBytes = rect.W * ImGuiTextureOp.BytesPerPixel;
        var pixels = new byte[rowBytes * rect.H];
        // UpdateRect is the bounding box of this frame's dirty glyphs — a sub-rect of a wider
        // texture, so it copies row by row rather than as one block.
        for (var row = 0; row < rect.H; row++)
        {
            var source = new ReadOnlySpan<byte>(texture.GetPixelsAt(rect.X, rect.Y + row), rowBytes);
            source.CopyTo(pixels.AsSpan(row * rowBytes));
        }
        return ImGuiTextureOp.Update(
            texture.GetTexID().Handle, rect.X, rect.Y, rect.W, rect.H, pixels);
    }

    private static ulong TextureIdOf(ImTextureDataPtr texture) => (ulong)texture.UniqueID + 1;

    private static void RequireRgba32(ImTextureDataPtr texture)
    {
        if (texture.Format != ImTextureFormat.Rgba32)
        {
            throw new NotSupportedException(
                $"ImGui texture {texture.UniqueID} is {texture.Format}; " +
                $"{nameof(ImGuiWebGpuRenderer)} uploads RGBA8 only. Leave " +
                "io.Fonts.TexDesiredFormat at its default, or teach the renderer the format.");
        }
    }
}
