using System;
using System.Numerics;
using Hexa.NET.ImGui;

namespace Paradise.Ui.ImGui;

/// <summary>Copies frame geometry into reusable buffers for transfer to the render
/// thread.</summary>
/// <remarks>ImGui invalidates draw lists at NewFrame. Commands address concatenated vertex/index
/// streams and require RendererHasVtxOffset; textures travel separately through
/// ImGuiTextureOps.</remarks>
public sealed class ImGuiDrawSnapshot
{
    /// <param name="ClipRect">Scissor rectangle in ImGui's display space.</param>
    /// <param name="TextureId">The <c>ImTextureID</c> to sample, as a plain integer — an id the
    /// renderer looks up, never a pointer into ImGui memory.</param>
    /// <param name="VertexOffset">First vertex, rebased onto the concatenated stream.</param>
    /// <param name="IndexOffset">First index, rebased onto the concatenated stream.</param>
    /// <param name="ElementCount">Indices to draw.</param>
    public readonly record struct Command(
        Vector4 ClipRect,
        ulong TextureId,
        uint VertexOffset,
        uint IndexOffset,
        uint ElementCount);

    /// <summary>sizeof(ImDrawVert): pos (2f) + uv (2f) + col (u32).</summary>
    public const int VertexStride = 20;

    public byte[] Vertices = Array.Empty<byte>();
    public int VertexBytes;
    public byte[] Indices = Array.Empty<byte>();
    public int IndexBytes;
    public Command[] Commands = Array.Empty<Command>();
    public int CommandCount;
    public Vector2 DisplayPosition;
    public Vector2 DisplaySize;
    public Vector2 FramebufferScale;

    /// <summary>Captures draw data on the ImGui thread after Render and before NewFrame.</summary>
    /// <remarks>Call ImGuiTextureCapture.CaptureFrom first to assign the texture IDs that command
    /// access requires.</remarks>
    public unsafe void Capture(ImDrawDataPtr drawData)
    {
        DisplayPosition = drawData.DisplayPos;
        DisplaySize = drawData.DisplaySize;
        FramebufferScale = drawData.FramebufferScale;
        VertexBytes = 0;
        IndexBytes = 0;
        CommandCount = 0;
        if (!drawData.Valid || drawData.CmdListsCount == 0 || drawData.TotalVtxCount == 0)
        {
            return;
        }

        EnsureCapacity(ref Vertices, drawData.TotalVtxCount * VertexStride);
        EnsureCapacity(ref Indices, drawData.TotalIdxCount * sizeof(ushort));

        var baseVertex = 0u;
        var baseIndex = 0u;
        for (var listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
        {
            var list = drawData.CmdLists[listIndex];

            var vertexCount = list.VtxBuffer.Size;
            new ReadOnlySpan<byte>(list.VtxBuffer.Data, vertexCount * VertexStride)
                .CopyTo(Vertices.AsSpan(VertexBytes));
            VertexBytes += vertexCount * VertexStride;

            var indexCount = list.IdxBuffer.Size;
            new ReadOnlySpan<byte>(list.IdxBuffer.Data, indexCount * sizeof(ushort))
                .CopyTo(Indices.AsSpan(IndexBytes));
            IndexBytes += indexCount * sizeof(ushort);

            for (var commandIndex = 0; commandIndex < list.CmdBuffer.Size; commandIndex++)
            {
                var cmd = list.CmdBuffer[commandIndex];
                if (cmd.UserCallback is not null || cmd.ElemCount == 0)
                {
                    continue; // user callbacks cannot cross threads; skip
                }
                if (CommandCount == Commands.Length)
                {
                    Array.Resize(ref Commands, Math.Max(16, Commands.Length * 2));
                }
                Commands[CommandCount++] = new Command(
                    cmd.ClipRect,
                    // GetTexID resolves ImTextureRef's two forms — a direct id, or a pointer to
                    // the ImTextureData whose id the capture side stamped — down to the integer.
                    cmd.GetTexID().Handle,
                    cmd.VtxOffset + baseVertex,
                    cmd.IdxOffset + baseIndex,
                    cmd.ElemCount);
            }

            baseVertex += (uint)vertexCount;
            baseIndex += (uint)indexCount;
        }
    }

    private static void EnsureCapacity(ref byte[] buffer, int bytes)
    {
        if (buffer.Length < bytes)
        {
            var size = Math.Max(1024, buffer.Length);
            while (size < bytes) size *= 2;
            Array.Resize(ref buffer, size);
        }
    }
}
