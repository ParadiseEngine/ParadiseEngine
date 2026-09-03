using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Paradise.Ui.ImGui;

/// <summary>The ordered handoff for Dear ImGui's texture protocol: the ImGui thread enqueues,
/// the render thread drains, and NOTHING IS EVER DROPPED.
///
/// <b>Why this is not the snapshot handoff.</b> <see cref="ImGuiDrawSnapshot"/>s are droppable
/// by design — the triple-buffered swap keeps only the newest and recycles the rest, because a
/// frame nobody drew is a frame nobody misses. Texture ops are the opposite: they are a state
/// machine, not a sample. Drop the <see cref="ImGuiTextureOpKind.Create"/> because the frame
/// that carried it was superseded and every later frame draws with a texture id the renderer
/// has never heard of — the font atlas silently disappears and nothing in the geometry path
/// says why. So ops ride their own queue, and the render thread drains it in order before it
/// draws.
///
/// <b>Ordering is load-bearing too</b>, and not only for correctness of the final image:
/// create-then-update-then-destroy on one id is a sequence where every step assumes the last
/// one happened. A queue, not a dictionary of latest-wins state.
///
/// A <see cref="ConcurrentQueue{T}"/> and no lock, because there is no compound state to guard:
/// an append is one operation and a drain is a run of them, so the queue's own atomicity is the
/// whole requirement. (<see cref="ImGuiFrameExchange"/> does take a lock — see there for what
/// makes that one different.) Coyote rewrites concurrent collections, so this stays schedulable
/// by <c>Paradise.Ui.ImGui.CoyoteTest</c>.
///
/// This type is deliberately free of any ImGui or GPU call — see
/// <see cref="ImGuiTextureCapture"/> for the native half. That split is what lets the Coyote
/// suite schedule it at all.</summary>
public sealed class ImGuiTextureOps
{
    private readonly ConcurrentQueue<ImGuiTextureOp> _pending = new();

    /// <summary>ImGui thread: append one operation.</summary>
    public void Enqueue(in ImGuiTextureOp op) => _pending.Enqueue(op);

    /// <summary>Render thread: move every pending operation onto the END of
    /// <paramref name="into"/>, in enqueue order, and return how many were appended.
    ///
    /// <b>Appends rather than clears, and that is the non-droppable rule reaching one step
    /// further.</b> A drain is destructive — the queue no longer has these ops — so the caller's
    /// list is now the only copy. A host that acquires a frame and then does not render it (no
    /// surface texture, a lost swapchain, a throw part-way) would drop exactly what this type
    /// exists to preserve, and the next frame would fail somewhere else entirely, blaming the
    /// queue. Appending makes the skip harmless: the ops stay in the list until something
    /// actually applies them, and <c>ImGuiWebGpuRenderer.ApplyTextureOps</c> is what clears it.
    ///
    /// Drains until momentarily empty rather than to a count fixed on entry, so an op enqueued
    /// mid-drain may ride along. That is not a hazard but the direction the slack has to fall:
    /// applying a texture the CURRENT snapshot does not name yet is free, while missing one it
    /// does name is the failure this queue exists to prevent.</summary>
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
