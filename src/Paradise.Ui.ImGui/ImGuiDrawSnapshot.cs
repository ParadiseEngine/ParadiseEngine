using System;
using System.Numerics;
using ImGuiNET;

namespace Paradise.Ui.ImGui;

/// <summary>A self-contained copy of one frame's <c>ImDrawData</c>, safe to hand across
/// threads: ImGui invalidates its draw lists on the next <c>NewFrame()</c>, so the UI thread
/// captures into one of these (buffers are reused and only grow) and the render thread draws
/// from it with no reference back into ImGui state. All command lists are concatenated into
/// single vertex/index streams; per-command vertex/index offsets are rebased accordingly
/// (requires the <c>RendererHasVtxOffset</c> backend flag on the ImGui context).</summary>
public sealed class ImGuiDrawSnapshot
{
    public readonly record struct Command(
        Vector4 ClipRect,
        nint TextureId,
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

    /// <summary>Capture the current <c>ImGui.GetDrawData()</c>. Call on the ImGui thread,
    /// after <c>ImGui.Render()</c> and before the next <c>NewFrame()</c>.</summary>
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
            new ReadOnlySpan<byte>((void*)list.VtxBuffer.Data, vertexCount * VertexStride)
                .CopyTo(Vertices.AsSpan(VertexBytes));
            VertexBytes += vertexCount * VertexStride;

            var indexCount = list.IdxBuffer.Size;
            new ReadOnlySpan<byte>((void*)list.IdxBuffer.Data, indexCount * sizeof(ushort))
                .CopyTo(Indices.AsSpan(IndexBytes));
            IndexBytes += indexCount * sizeof(ushort);

            for (var commandIndex = 0; commandIndex < list.CmdBuffer.Size; commandIndex++)
            {
                var cmd = list.CmdBuffer[commandIndex];
                if (cmd.UserCallback != IntPtr.Zero || cmd.ElemCount == 0)
                {
                    continue; // user callbacks cannot cross threads; skip
                }
                if (CommandCount == Commands.Length)
                {
                    Array.Resize(ref Commands, Math.Max(16, Commands.Length * 2));
                }
                Commands[CommandCount++] = new Command(
                    cmd.ClipRect,
                    cmd.TextureId,
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
