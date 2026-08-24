using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;
using Paradise.Rendering.WebGPU;
using Paradise.Rendering.WebGPU.Internal;

namespace Paradise.Rendering.WebGPU.CoyoteTest;

/// <summary>
/// <see cref="CaptureQueue"/> under systematic exploration.
///
/// This exists because a hand-written race test could not do the job. The defect these pin — a
/// request enqueued after the drain had already passed it by, leaving a task nobody would ever
/// complete — has a window a few instructions wide, and a stress loop written against the BROKEN
/// code passed three runs out of three. Coyote schedules the interleavings rather than hoping for
/// them, so the bad one is reached on purpose and comes back replayable.
///
/// THE INVARIANT, in one sentence: every request the queue ACCEPTS is either handed to a frame or
/// faulted, and one it REFUSES was never accepted. What must never exist is a task that is neither
/// served, faulted, nor refused — that is a caller hung for the life of the process.
/// </summary>
public static class CaptureQueueTests
{
    private static readonly Exception Closed = new ObjectDisposedException("renderer");

    /// <summary>
    /// A producer racing a close: everything accepted must end up faulted.
    ///
    /// The shape of the real bug. One thread posts requests while another closes the queue, which
    /// is exactly what happens when a host disposes a renderer while a capture is in flight.
    /// </summary>
    [Test]
    public static async Task EnqueueRacingClose_LeavesNothingPending()
    {
        var queue = new CaptureQueue();
        var accepted = new List<TaskCompletionSource<ColorReadback>>();
        var acceptedGate = new object();

        var producer = Task.Run(() =>
        {
            for (var i = 0; i < 4; i++)
            {
                var request = new TaskCompletionSource<ColorReadback>();
                if (!queue.TryEnqueue(request))
                {
                    // Refused: legal, and the caller is told rather than left waiting.
                    Specification.Assert(!request.Task.IsCompleted,
                        "A refused request must not have been completed by the queue.");
                    continue;
                }
                lock (acceptedGate)
                {
                    accepted.Add(request);
                }
            }
        });

        var closer = Task.Run(() => queue.CloseAndFault(Closed));

        // AWAITED, not blocked on. A Task.WaitAll here parks the thread, which Coyote cannot
        // tell apart from a deadlock — it reported every one of these as a potential hang before,
        // including against correct code. Awaiting lets it schedule the join like any other point.
        await Task.WhenAll(producer, closer).ConfigureAwait(false);

        // Anything the queue took must be settled by the close — nothing may still be waiting.
        lock (acceptedGate)
        {
            foreach (var request in accepted)
            {
                Specification.Assert(request.Task.IsCompleted,
                    "A request the queue accepted was left pending after close: that task hangs its caller forever.");
            }
        }
    }

    /// <summary>
    /// A frame draining while a producer posts and a third thread closes.
    ///
    /// Closer to the live arrangement than the pair above: the render thread takes requests to
    /// serve while callers add and a host disposes. A request may legally be served (dequeued) or
    /// faulted; it may not vanish.
    /// </summary>
    [Test]
    public static async Task DrainRacingEnqueueAndClose_ServesOrFaultsEveryRequest()
    {
        var queue = new CaptureQueue();
        var accepted = new List<TaskCompletionSource<ColorReadback>>();
        var served = new List<TaskCompletionSource<ColorReadback>>();
        var gate = new object();

        var producer = Task.Run(() =>
        {
            for (var i = 0; i < 3; i++)
            {
                var request = new TaskCompletionSource<ColorReadback>();
                if (queue.TryEnqueue(request))
                {
                    lock (gate)
                    {
                        accepted.Add(request);
                    }
                }
            }
        });

        var frame = Task.Run(() =>
        {
            for (var i = 0; i < 3; i++)
            {
                if (queue.TryDequeue(out var request))
                {
                    // What a real frame does: serve it.
                    request.TrySetResult(default);
                    lock (gate)
                    {
                        served.Add(request);
                    }
                }
            }
        });

        var closer = Task.Run(() => queue.CloseAndFault(Closed));

        await Task.WhenAll(producer, frame, closer).ConfigureAwait(false);

        lock (gate)
        {
            foreach (var request in accepted)
            {
                Specification.Assert(request.Task.IsCompleted,
                    "An accepted request was neither served by a frame nor faulted by the close.");
            }
        }
    }

    /// <summary>Closing twice from two threads faults each request once and does not throw — a
    /// host may dispose from anywhere, and a second close is a no-op rather than a second round of
    /// faults on tasks already settled.</summary>
    [Test]
    public static async Task ConcurrentCloses_AreIdempotent()
    {
        var queue = new CaptureQueue();
        var request = new TaskCompletionSource<ColorReadback>();
        Specification.Assert(queue.TryEnqueue(request), "The queue should accept while open.");

        var first = Task.Run(() => queue.CloseAndFault(Closed));
        var second = Task.Run(() => queue.CloseAndFault(Closed));
        await Task.WhenAll(first, second).ConfigureAwait(false);

        Specification.Assert(request.Task.IsCompleted, "The queued request must be faulted by the close.");
        Specification.Assert(!queue.TryEnqueue(new TaskCompletionSource<ColorReadback>()),
            "A closed queue must refuse new requests, or they would be stranded.");
    }
}
