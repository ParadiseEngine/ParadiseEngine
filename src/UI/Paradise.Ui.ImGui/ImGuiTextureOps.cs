using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Paradise.Ui.ImGui;

/// <summary>Queues texture operations in order without dropping superseded frames'
/// operations.</summary>
/// <remarks>Create, update and destroy depend on prior entries, so latest-wins coalescing is
/// invalid. ConcurrentQueue provides the required atomic operations; native calls stay in
/// ImGuiTextureCapture so Coyote can schedule this queue.</remarks>
public sealed class ImGuiTextureOps
{
    private readonly ConcurrentQueue<ImGuiTextureOp> _pending = new();

    /// <summary>ImGui thread: append one operation.</summary>
    public void Enqueue(in ImGuiTextureOp op) => _pending.Enqueue(op);

    /// <summary>Appends pending operations to the render thread's list and returns the number
    /// moved.</summary>
    /// <remarks>Only ApplyTextureOps clears the list, preserving pending work across skipped
    /// frames. Drain until momentarily empty; operations enqueued during the drain may be
    /// included.</remarks>
    public int DrainTo(List<ImGuiTextureOp> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        var drained = 0;
        while (_pending.TryDequeue(out var op))
        {
            into.Add(op);
            drained++;
        }
        return drained;
    }

    /// <summary>How many operations are waiting. Diagnostics only — a caller that branches on
    /// this before enqueuing or draining has reintroduced a check-then-act race.</summary>
    public int PendingCount => _pending.Count;
}
