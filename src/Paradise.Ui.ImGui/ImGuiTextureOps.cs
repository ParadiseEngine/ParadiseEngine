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
/// Locking is on a plain <c>object</c> rather than <c>System.Threading.Lock</c>: Coyote (1.7.11)
/// rewrites <c>Monitor.Enter</c>/<c>Exit</c> and cannot intercept <c>Lock.EnterScope</c>, so the
/// newer type would make every interleaving around this queue invisible to
/// <c>Paradise.Ui.ImGui.CoyoteTest</c>. The lock is cold (a handful of ops per session), so
/// there is nothing to win by it anyway.
///
/// This type is deliberately free of any ImGui or GPU call — see
/// <see cref="ImGuiTextureCapture"/> for the native half. That split is what lets the Coyote
/// suite schedule it at all.</summary>
public sealed class ImGuiTextureOps
{
    private readonly object _lock = new();
    private readonly Queue<ImGuiTextureOp> _pending = new();

    /// <summary>ImGui thread: append one operation. Never blocks on the render thread beyond
    /// the lock.</summary>
    public void Enqueue(in ImGuiTextureOp op)
    {
        lock (_lock)
        {
            _pending.Enqueue(op);
        }
    }

    /// <summary>Render thread: move every pending operation into <paramref name="into"/>, in
    /// enqueue order, and return how many. <paramref name="into"/> is CLEARED first, so callers
    /// can keep one scratch list for the process lifetime.
    ///
    /// Draining into a caller-owned list rather than returning a snapshot keeps the lock held
    /// for the copy only — applying an op touches the GPU, and holding a lock across that would
    /// stall the ImGui thread on the render thread's slowest work.</summary>
    public int DrainTo(List<ImGuiTextureOp> into)
    {
        into.Clear();
        lock (_lock)
        {
            while (_pending.Count > 0)
            {
                into.Add(_pending.Dequeue());
            }
        }
        return into.Count;
    }

    /// <summary>How many operations are waiting. Diagnostics only — a caller that branches on
    /// this before enqueuing or draining has reintroduced the check-then-act race the Coyote
    /// suite exists to catch.</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }
}
