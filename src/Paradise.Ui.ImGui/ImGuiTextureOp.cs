using System;

namespace Paradise.Ui.ImGui;

/// <summary>What one <see cref="ImGuiTextureOp"/> asks the renderer to do.</summary>
public enum ImGuiTextureOpKind : byte
{
    /// <summary>Allocate a texture of <see cref="ImGuiTextureOp.Width"/> ×
    /// <see cref="ImGuiTextureOp.Height"/> under <see cref="ImGuiTextureOp.TextureId"/> and
    /// upload the whole of <see cref="ImGuiTextureOp.Pixels"/> into it.</summary>
    Create = 0,

    /// <summary>Overwrite the sub-rectangle at (<see cref="ImGuiTextureOp.X"/>,
    /// <see cref="ImGuiTextureOp.Y"/>) sized <see cref="ImGuiTextureOp.Width"/> ×
    /// <see cref="ImGuiTextureOp.Height"/> with <see cref="ImGuiTextureOp.Pixels"/>.</summary>
    Update = 1,

    /// <summary>Release the texture under <see cref="ImGuiTextureOp.TextureId"/>. The renderer
    /// defers the actual free past the last frame that could still reference it.</summary>
    Destroy = 2,
}

/// <summary>One self-contained instruction from Dear ImGui's 1.92 texture protocol, captured on
/// the ImGui thread and applied on the render thread.
///
/// <b>Self-contained is the whole point.</b> ImGui hands out an <c>ImTextureData*</c> whose
/// pixel buffer it owns and frees on its own schedule; the render thread must never see that
/// pointer. So the pixels are COPIED here at capture time, and what crosses the thread boundary
/// is a plain managed record with no reference back into ImGui state — the same discipline
/// <see cref="ImGuiDrawSnapshot"/> applies to geometry.</summary>
/// <param name="Kind">Which operation this is; says which other fields mean anything.</param>
/// <param name="TextureId">The <c>ImTextureID</c> the renderer keys this texture by. For an
/// ImGui-owned texture this is its <c>ImTextureData.UniqueID</c> plus one (0 is ImGui's null
/// id); host textures live at or above
/// <see cref="ImGuiWebGpuRenderer.FirstHostTextureId"/> and never collide with it.</param>
/// <param name="Width">Create: the texture's width. Update: the update rect's width.</param>
/// <param name="Height">Create: the texture's height. Update: the update rect's height.</param>
/// <param name="X">Update: the rect's left edge in the destination texture. 0 otherwise.</param>
/// <param name="Y">Update: the rect's top edge in the destination texture. 0 otherwise.</param>
/// <param name="Pixels">Tightly packed RGBA8 for the region above — exactly
/// <c>Width * Height * 4</c> bytes, no row padding. Empty for
/// <see cref="ImGuiTextureOpKind.Destroy"/>.</param>
public readonly record struct ImGuiTextureOp(
    ImGuiTextureOpKind Kind,
    ulong TextureId,
    uint Width,
    uint Height,
    uint X,
    uint Y,
    byte[] Pixels)
{
    /// <summary>Bytes per pixel in <see cref="Pixels"/>. The capture side rejects any other
    /// <c>ImTextureFormat</c>, so this is a constant rather than a field.</summary>
    public const int BytesPerPixel = 4;

    public static ImGuiTextureOp Create(ulong textureId, uint width, uint height, byte[] pixels) =>
        new(ImGuiTextureOpKind.Create, textureId, width, height, 0, 0, pixels);

    public static ImGuiTextureOp Update(ulong textureId, uint x, uint y, uint width, uint height, byte[] pixels) =>
        new(ImGuiTextureOpKind.Update, textureId, width, height, x, y, pixels);

    public static ImGuiTextureOp Destroy(ulong textureId) =>
        new(ImGuiTextureOpKind.Destroy, textureId, 0, 0, 0, 0, Array.Empty<byte>());
}
