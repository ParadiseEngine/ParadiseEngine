using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Frame-deferred release queue. <see cref="Schedule"/> queues a release callback for
/// <c>frameNumber + maxFramesInFlight</c> frames in the future; <see cref="DrainCompleted"/> runs
/// every release whose target frame has elapsed. Holding releases for a few frames lets us safely
/// destroy resources still referenced by in-flight GPU work.</summary>
internal sealed partial class DeferredDestructionQueue
{
    private readonly int _maxFramesInFlight;
    private readonly ILogger _log;
    private readonly Queue<Pending> _pending = new();
    private ulong _currentFrame;

    private readonly struct Pending
    {
        public readonly ulong ReleaseAtFrame;
        public readonly Action Release;

        public Pending(ulong releaseAt, Action release)
        {
            ReleaseAtFrame = releaseAt;
            Release = release;
        }
    }

    public DeferredDestructionQueue(int maxFramesInFlight, ILogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFramesInFlight, 1);
        _maxFramesInFlight = maxFramesInFlight;
        _log = logger ?? NullLogger.Instance;
    }

    public ulong CurrentFrame => _currentFrame;

    public int PendingCount => _pending.Count;

    /// <summary>Queue a release to run after <c>maxFramesInFlight</c> frames have advanced.</summary>
    public void Schedule(Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        _pending.Enqueue(new Pending(_currentFrame + (ulong)_maxFramesInFlight, release));
    }

    /// <summary>Advance the frame counter. Call once per <c>EndFrame</c>.</summary>
    public void AdvanceFrame()
    {
        _currentFrame++;
        DrainCompleted();
    }

    /// <summary>Run any pending release whose target frame has been reached.</summary>
    public void DrainCompleted()
    {
        while (_pending.Count > 0 && _pending.Peek().ReleaseAtFrame <= _currentFrame)
        {
            var pending = _pending.Dequeue();
            try { pending.Release(); }
            catch (Exception ex)
            {
                LogReleaseThrew(_log, ex);
            }
        }
    }

    /// <summary>Run every queued release immediately, ignoring the deferred-frame schedule. Used at
    /// renderer disposal to release everything before the device goes away.</summary>
    public void DrainAll()
    {
        while (_pending.Count > 0)
        {
            var pending = _pending.Dequeue();
            try { pending.Release(); }
            catch (Exception ex)
            {
                LogReleaseThrew(_log, ex);
            }
        }
    }

    // The exception goes in the exception slot rather than interpolated into the message: a sink
    // that wants the stack gets it, and a sink that wants one line is not handed a whole trace.
    [LoggerMessage(EventId = 60, Level = LogLevel.Error, Message = "release callback threw")]
    private static partial void LogReleaseThrew(ILogger logger, Exception exception);
}
