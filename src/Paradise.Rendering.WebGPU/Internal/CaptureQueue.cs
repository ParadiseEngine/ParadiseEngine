using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Coordinates capture requests between callers and the render thread.</summary>
/// <remarks>Every accepted request must be served or faulted. Enqueue and close share a lock so no
/// request can arrive after the final drain. Native calls stay outside this managed type so Coyote
/// can schedule its interleavings.</remarks>
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
