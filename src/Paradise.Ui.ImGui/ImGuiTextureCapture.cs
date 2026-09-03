using System;
using Hexa.NET.ImGui;

namespace Paradise.Ui.ImGui;

/// <summary>The ImGui-thread half of Dear ImGui 1.92's texture protocol: read
/// <c>ImDrawData.Textures</c>, copy out whatever the renderer has to upload, and answer ImGui
/// with a status so it stops asking.
///
/// <b>The protocol, and why the answer goes back here rather than on the render thread.</b>
/// ImGui asks for work by putting a status on an <c>ImTextureData</c> it owns:
/// <c>WantCreate</c> → allocate and upload everything, then tell it the id you allocated;
/// <c>WantUpdates</c> → re-upload <c>UpdateRect</c>; <c>WantDestroy</c> → free it. The obvious
/// backend writes those replies from wherever it did the GPU work — which for us is the render
/// thread, mid-flight, against a struct the ImGui thread is simultaneously rebuilding in
/// <c>NewFrame</c>. So the replies are written HERE, immediately, on the ImGui thread, and the
/// renderer never sees an <c>ImTextureData</c> at all: it sees a queue of
/// <see cref="ImGuiTextureOp"/>s with the pixels already copied. Answering optimistically is
/// safe because <see cref="ImGuiTextureOps"/> does not drop ops — the upload is guaranteed to
/// happen, just not yet.</summary>
public static class ImGuiTextureCapture
{
    /// <summary>Capture this frame's texture work into <paramref name="ops"/> and mark every
    /// request answered. ImGui thread only, after <c>ImGui.Render()</c> and before the next
    /// <c>NewFrame()</c> — the same window <see cref="ImGuiDrawSnapshot.Capture"/> requires.</summary>
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
                    // The id ImGui will stamp into every ImDrawCmd that samples this texture.
                    // Offset by one because 0 is ImGui's null id and a command carrying it
                    // asserts at draw time. UniqueID is ImGui's own counter and stays far below
                    // ImGuiWebGpuRenderer.FirstHostTextureId, so it cannot collide with a
                    // host-registered texture.
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
                    // Clear the id BEFORE reporting Destroyed. ImGui's atlas asserts that a
                    // destroyed texture carries no id before it removes the ImTextureData, and
                    // every official backend clears it here. Hexa ships release natives, so the
                    // assert is compiled out today and this reads as cosmetic — it is not: it is
                    // a hard abort against any debug native.
                    texture.SetTexID(ImTextureID.Null);
                    texture.SetStatus(ImTextureStatus.Destroyed);
                    // Deliberately NOT gated on UnusedFrames > 0, which is how the official
                    // backends decline to free a texture ImGui drew with this frame. We answer
                    // immediately and defer on our own side instead: the ops queue is ordered and
                    // ImGuiWebGpuRenderer holds both the texture and its lookup for
                    // DestroyDelayFrames, which covers the same window without making ImGui wait.
                    break;

                case ImTextureStatus.Ok:
                case ImTextureStatus.Destroyed:
                default:
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
