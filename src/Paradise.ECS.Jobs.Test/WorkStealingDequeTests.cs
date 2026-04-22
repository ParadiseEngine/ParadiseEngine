using System.Collections.Concurrent;

namespace Paradise.ECS.Jobs.Test;

/// <summary>
/// Unit tests for <see cref="WorkStealingDeque"/> — the Chase-Lev work-stealing deque.
/// Covers LIFO/FIFO ordering, empty states, growth, reset, and concurrent correctness.
/// </summary>
public sealed class WorkStealingDequeTests
{
    // ---- Basic Ordering ----

    [Test]
    public async Task PopBottom_MultipleItems_ReturnsInLifoOrder()
    {
        var deque = new WorkStealingDeque(8);
        for (int i = 0; i < 5; i++)
            deque.PushBottom(i);

        for (int i = 4; i >= 0; i--)
            await Assert.That(deque.PopBottom()).IsEqualTo(i);
    }

    [Test]
    public async Task Steal_MultipleItems_ReturnsInFifoOrder()
    {
        var deque = new WorkStealingDeque(8);
        for (int i = 0; i < 5; i++)
            deque.PushBottom(i);

        for (int i = 0; i < 5; i++)
            await Assert.That(deque.Steal()).IsEqualTo(i);
    }

    // ---- Empty Deque ----

    [Test]
    public async Task PopBottom_EmptyDeque_ReturnsNegativeOne()
    {
        var deque = new WorkStealingDeque(4);
        await Assert.That(deque.PopBottom()).IsEqualTo(-1);
    }

    [Test]
    public async Task Steal_EmptyDeque_ReturnsNegativeOne()
    {
        var deque = new WorkStealingDeque(4);
        await Assert.That(deque.Steal()).IsEqualTo(-1);
    }

    [Test]
    public async Task PopBottom_AfterDrain_ReturnsNegativeOne()
    {
        var deque = new WorkStealingDeque(4);
        deque.PushBottom(42);
        deque.PopBottom();
        await Assert.That(deque.PopBottom()).IsEqualTo(-1);
    }

    [Test]
    public async Task Steal_AfterDrain_ReturnsNegativeOne()
    {
        var deque = new WorkStealingDeque(4);
        deque.PushBottom(42);
        deque.Steal();
        await Assert.That(deque.Steal()).IsEqualTo(-1);
    }

    // ---- Count ----

    [Test]
    public async Task Count_AfterPushAndPop_ReflectsCurrentSize()
    {
        var deque = new WorkStealingDeque(8);
        await Assert.That(deque.Count).IsEqualTo(0);

        deque.PushBottom(1);
        deque.PushBottom(2);
        deque.PushBottom(3);
        await Assert.That(deque.Count).IsEqualTo(3);

        deque.PopBottom();
        await Assert.That(deque.Count).IsEqualTo(2);

        deque.Steal();
        await Assert.That(deque.Count).IsEqualTo(1);
    }

    // ---- Growth ----

    [Test]
    public async Task PushBottom_ExceedsInitialCapacity_GrowsAndRetainsAllItems()
    {
        var deque = new WorkStealingDeque(4); // initial capacity 4
        const int count = 100;

        for (int i = 0; i < count; i++)
            deque.PushBottom(i);

        int retrieved = deque.Count;
        await Assert.That(retrieved).IsEqualTo(count);

        // Pop all in LIFO order to verify data integrity after growth
        for (int i = count - 1; i >= 0; i--)
            await Assert.That(deque.PopBottom()).IsEqualTo(i);
    }

    // ---- Reset ----

    [Test]
    public async Task Reset_ClearsDeque_ThenPushPopWorks()
    {
        var deque = new WorkStealingDeque(8);
        deque.PushBottom(10);
        deque.PushBottom(20);

        deque.Reset();
        await Assert.That(deque.Count).IsEqualTo(0);
        await Assert.That(deque.PopBottom()).IsEqualTo(-1);
        await Assert.That(deque.Steal()).IsEqualTo(-1);

        // Push/pop works after reset
        deque.PushBottom(30);
        await Assert.That(deque.PopBottom()).IsEqualTo(30);
    }

    [Test]
    public async Task Reset_WithLargerCapacity_ExpandsBuffer()
    {
        var deque = new WorkStealingDeque(4);
        deque.Reset(capacity: 128);

        // Should accommodate 128 items without growing
        for (int i = 0; i < 128; i++)
            deque.PushBottom(i);

        await Assert.That(deque.Count).IsEqualTo(128);
    }

    // ---- Single-Element Race ----

    [Test]
    public async Task SingleElement_ConcurrentPopAndSteal_ExactlyOneSucceeds()
    {
        const int iterations = 10_000;
        int bothSucceeded = 0;
        int neitherSucceeded = 0;

        for (int iter = 0; iter < iterations; iter++)
        {
            var deque = new WorkStealingDeque(4);
            deque.PushBottom(42);

            int popResult = -1;
            int stealResult = -1;

            using var barrier = new Barrier(2);

            var stealerThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                stealResult = deque.Steal();
            });

            stealerThread.Start();
            barrier.SignalAndWait();
            popResult = deque.PopBottom();
            stealerThread.Join();

            int successes = (popResult >= 0 ? 1 : 0) + (stealResult >= 0 ? 1 : 0);
            if (successes == 2) bothSucceeded++;
            if (successes == 0) neitherSucceeded++;
        }

        // Exactly one must succeed per iteration — never both, never neither
        await Assert.That(bothSucceeded).IsEqualTo(0);
        await Assert.That(neitherSucceeded).IsEqualTo(0);
    }

    // ---- Concurrent Stress ----

    [Test]
    public async Task ConcurrentPushPopAndSteal_AllItemsAccountedFor()
    {
        const int itemCount = 50_000;
        const int stealerCount = 4;
        var deque = new WorkStealingDeque(64);
        var collected = new ConcurrentBag<int>();

        using var startBarrier = new Barrier(stealerCount + 1);

        // Stealer threads
        var stealers = new Thread[stealerCount];
        var stealerDone = new bool[1]; // signal stealers to stop via Volatile.Read/Write
        for (int s = 0; s < stealerCount; s++)
        {
            stealers[s] = new Thread(() =>
            {
                startBarrier.SignalAndWait();
                while (!Volatile.Read(ref stealerDone[0]))
                {
                    int item = deque.Steal();
                    if (item >= 0)
                        collected.Add(item);
                }
                // Drain remaining
                int remaining;
                while ((remaining = deque.Steal()) >= 0)
                    collected.Add(remaining);
            });
            stealers[s].Start();
        }

        // Owner thread: push all, then pop remaining
        startBarrier.SignalAndWait();
        for (int i = 0; i < itemCount; i++)
        {
            deque.PushBottom(i);
            // Occasionally pop from our own end to simulate real owner behavior
            if (i % 7 == 0)
            {
                int popped = deque.PopBottom();
                if (popped >= 0)
                    collected.Add(popped);
            }
        }

        // Drain owner's remaining items
        int item2;
        while ((item2 = deque.PopBottom()) >= 0)
            collected.Add(item2);

        Volatile.Write(ref stealerDone[0], true);
        foreach (var t in stealers)
            t.Join();

        // Verify every item retrieved exactly once
        var seen = new HashSet<int>(collected);
        await Assert.That(seen.Count).IsEqualTo(itemCount);
        for (int i = 0; i < itemCount; i++)
            await Assert.That(seen.Contains(i)).IsTrue();
    }
}
