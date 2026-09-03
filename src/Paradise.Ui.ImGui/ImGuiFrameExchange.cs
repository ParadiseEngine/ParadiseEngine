using System;
using System.Collections.Generic;

namespace Paradise.Ui.ImGui;

/// <summary>The whole sim-thread → render-thread handoff for one ImGui frame: the triple-buffered
/// snapshot slot and the texture-op queue, together, because their ORDER relative to each other
/// is the invariant neither can state alone.
///
/// <b>The rule: take the snapshot first, then drain the ops.</b> A frame is published as ops
/// first, snapshot second (<see cref="TextureOps"/>.Enqueue then <see cref="Publish"/>), so every op
/// a given snapshot depends on was already in the queue before that snapshot became visible.
/// Draining after the swap therefore guarantees the render thread holds every texture its
/// snapshot names. Draining BEFORE the swap does not: the sim thread can publish a whole new
/// frame in between, and the renderer then draws a texture id it has never allocated — a draw
/// that silently disappears, with nothing in the geometry path to say why. That is why
/// <see cref="AcquireForRender"/> does both steps rather than leaving the order to the host.
///
/// Snapshots are droppable and ops are not, and both facts live here: a superseded snapshot goes
/// back to the free pool to be overwritten, while its ops stay queued until the render thread has
/// applied them.
///
/// <b>Why a lock, where <see cref="ImGuiTextureOps"/> needs none.</b> This is not a queue but a
/// three-slot state machine, and every transition touches more than one slot at once: publishing
/// recycles the old latest AND installs the new one; acquiring retires the current rendering AND
/// promotes AND clears latest. Making each slot individually atomic — a <c>ConcurrentStack</c>
/// for the pool — would leave the race intact: a publish that reads <c>_latest</c>, loses the
/// thread to an acquire that promotes that same snapshot, and then recycles it, hands the sim
/// thread the buffer the render thread is drawing. An <c>Interlocked.Exchange</c> pair would in
/// fact be correct here, and is not used: it buys nothing at two pointer swaps per frame, and it
/// would rest on an unwritten "render thread only" rule for <c>_rendering</c> that the lock makes
/// unnecessary to argue.
///
/// The lock is a plain <c>object</c> rather than <c>System.Threading.Lock</c> so that
/// <c>Paradise.Ui.ImGui.CoyoteTest</c> can schedule it — Coyote 1.7.11 rewrites
/// <c>Monitor.Enter</c>/<c>Exit</c> and does not intercept <c>Lock.EnterScope</c>.</summary>
public sealed class ImGuiFrameExchange
{
    private readonly object _lock = new();
    private readonly Stack<ImGuiDrawSnapshot> _free = new();
    private ImGuiDrawSnapshot? _latest;
    private ImGuiDrawSnapshot? _rendering;

    /// <summary>The PRODUCER side of the texture queue: enqueue a frame's ops here before
    /// publishing that frame's snapshot. Do not drain it directly — draining outside
    /// <see cref="AcquireForRender"/> is exactly the ordering this type exists to prevent.</summary>
    public ImGuiTextureOps TextureOps { get; } = new();

    /// <summary>Sim thread: a snapshot to capture into — recycled from the pool when one is
    /// spare. Pair every rent with a <see cref="Publish"/>.</summary>
    public ImGuiDrawSnapshot Rent()
    {
        lock (_lock)
        {
            return _free.Count > 0 ? _free.Pop() : new ImGuiDrawSnapshot();
        }
    }

    /// <summary>Sim thread: make <paramref name="snapshot"/> the newest frame. A previous frame
    /// the render thread never took is recycled — dropping it is correct, since nobody misses a
    /// frame that was never shown.</summary>
    public void Publish(ImGuiDrawSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_lock)
        {
            if (_latest is not null) _free.Push(_latest);
            _latest = snapshot;
        }
    }

    /// <summary>Render thread: take the newest frame and everything needed to draw it.
    ///
    /// Apply <paramref name="textureOps"/> before drawing the returned snapshot. The list is
    /// CLEARED and refilled, so one scratch list serves for the process lifetime.</summary>
    /// <param name="textureOps">Receives every texture operation not yet applied, in order.</param>
    /// <param name="isNew">False when this is the same snapshot as the previous call — hosts with
    /// retained scenes (Godot canvas items) skip the rebuild then. It says nothing about
    /// <paramref name="textureOps"/>, which must be applied either way.</param>
    /// <returns>The snapshot to draw, or null before the first published frame.</returns>
    public ImGuiDrawSnapshot? AcquireForRender(List<ImGuiTextureOp> textureOps, out bool isNew)
    {
        ArgumentNullException.ThrowIfNull(textureOps);
        ImGuiDrawSnapshot? snapshot;
        lock (_lock)
        {
            isNew = _latest is not null;
            if (_latest is not null)
            {
                if (_rendering is not null) _free.Push(_rendering);
                _rendering = _latest;
                _latest = null;
            }
            snapshot = _rendering;
        }
        // Strictly after the swap. See the type's remarks — this line is the invariant.
        TextureOps.DrainTo(textureOps);
        return snapshot;
    }
}
