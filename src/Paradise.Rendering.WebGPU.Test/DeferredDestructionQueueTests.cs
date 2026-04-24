using System;
using Paradise.Rendering.WebGPU.Internal;

namespace Paradise.Rendering.WebGPU.Test;

public class DeferredDestructionQueueTests
{
    [Test]
    public async Task release_runs_after_max_frames_in_flight()
    {
        var q = new DeferredDestructionQueue(maxFramesInFlight: 2);
        var fired = false;
        q.Schedule(() => fired = true);

        // Frame 0 -> Schedule. ReleaseAt = 0 + 2 = 2.
        // Frame 1: AdvanceFrame -> currentFrame=1; pending releaseAt=2 > 1, no-op.
        q.AdvanceFrame();
        await Assert.That(fired).IsFalse();

        // Frame 2: AdvanceFrame -> currentFrame=2; releaseAt=2 <= 2, fires.
        q.AdvanceFrame();
        await Assert.That(fired).IsTrue();
        await Assert.That(q.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task drain_all_fires_every_pending_release_immediately()
    {
        var q = new DeferredDestructionQueue(maxFramesInFlight: 4);
        var fires = 0;
        q.Schedule(() => fires++);
        q.Schedule(() => fires++);
        q.Schedule(() => fires++);

        q.DrainAll();
        await Assert.That(fires).IsEqualTo(3);
        await Assert.That(q.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task release_callback_throwing_does_not_break_queue()
    {
        var q = new DeferredDestructionQueue(maxFramesInFlight: 1);
        var ranAfter = false;
        q.Schedule(() => throw new InvalidOperationException("boom"));
        q.Schedule(() => ranAfter = true);

        q.AdvanceFrame();
        await Assert.That(ranAfter).IsTrue();
    }

    [Test]
    public async Task ctor_rejects_zero_or_negative_frames_in_flight()
    {
        await Assert.That(() => new DeferredDestructionQueue(0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new DeferredDestructionQueue(-1)).Throws<ArgumentOutOfRangeException>();
    }
}
