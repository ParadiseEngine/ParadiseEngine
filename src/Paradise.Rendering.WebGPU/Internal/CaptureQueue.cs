using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>
/// Capture requests waiting for a frame, and the one piece of genuine cross-thread coordination in
/// the renderer.
///
/// It is its own type for two reasons, and the second is why it is worth the file. The first is
/// ordinary: requests arrive from any thread while the render thread services them, so the rules
/// belong together rather than scattered across a class whose every other member is single-threaded.
///
/// The second is that this is the part that can be TESTED. Everything else in the capture path is
/// Dawn calls a systematic-testing tool cannot schedule; this is plain managed concurrency, so
/// <c>Paradise.Rendering.WebGPU.CoyoteTest</c> can explore its interleavings deliberately instead
/// of hoping a stress loop lands on the bad one. It was extracted precisely because a hand-written
/// race test could not reproduce the defect below — it passed against the broken code every time.
///
/// <b>The invariant: a request is either served, or faulted — never left pending.</b> A task nobody
/// will ever complete is a caller hung for the life of the process, which is the failure this whole
/// capture path exists to avoid. That is why closing and enqueueing share a lock: without it a
/// caller can pass the "still open?" check, lose the race to a close that drains everything it can
/// see, and then enqueue into a queue nothing will look at again.
/// </summary>
internal sealed class CaptureQueue
{
    private readonly ConcurrentQueue<TaskCompletionSource<ColorReadback>> _requests = new();

    /// <summary>Guards <see cref="_closed"/> together with the enqueue and the drain, making
    /// "check then add" and "close then empty" each ONE step with respect to the other.</summary>
    private readonly object _gate = new();

    private bool _closed;

    /// <summary>Nothing is waiting. Checked before a frame does any work for the queue, which is
    /// the common case.</summary>
    public bool IsEmpty => _requests.IsEmpty;

    /// <summary>
    /// Take a request, or refuse it because the queue is closed.
    /// </summary>
    /// <returns>False when closed — the caller has NOT been queued and must be told so. Returning
    /// a task that silently never completes is the one outcome this type exists to prevent.</returns>
    public bool TryEnqueue(TaskCompletionSource<ColorReadback> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_closed)
            {
                return false;
            }
            _requests.Enqueue(request);
            return true;
        }
    }

    /// <summary>Take the next request for a frame to serve, if any. Called only by the render
    /// thread, between frames.</summary>
    public bool TryDequeue(out TaskCompletionSource<ColorReadback> request) =>
        _requests.TryDequeue(out request!);

    /// <summary>
    /// Close the queue and fault everything still in it.
    ///
    /// After this, <see cref="TryEnqueue"/> refuses, so nothing can be stranded by arriving late.
    /// Idempotent: closing twice is a no-op rather than a second round of faults.
    /// </summary>
    public void CloseAndFault(Exception reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
            while (_requests.TryDequeue(out var abandoned))
            {
                abandoned.TrySetException(reason);
            }
        }
    }
}
