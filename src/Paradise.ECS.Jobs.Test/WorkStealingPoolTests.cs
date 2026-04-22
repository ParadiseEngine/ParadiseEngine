namespace Paradise.ECS.Jobs.Test;

/// <summary>
/// Tests for <see cref="WorkStealingPool"/> — work-stealing thread pool.
/// Mirrors <see cref="JobWorkerPoolTests"/> for API parity.
/// </summary>
public sealed class WorkStealingPoolTests
{
    // ---- Basic Functionality ----

    [Test]
    public async Task ExecuteWork_ZeroItems_IsNoOp()
    {
        using var pool = new WorkStealingPool(2);
        var invoked = false;
        pool.ExecuteWork(DelegateWorkItem.Create(0, _ => invoked = true));
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task ExecuteWork_SingleItem_ExecutesOnCallingThread()
    {
        using var pool = new WorkStealingPool(2);
        var callingThreadId = Environment.CurrentManagedThreadId;
        var executedThreadId = -1;

        pool.ExecuteWork(DelegateWorkItem.Create(1, _ => executedThreadId = Environment.CurrentManagedThreadId));

        await Assert.That(executedThreadId).IsEqualTo(callingThreadId);
    }

    [Test]
    public async Task ExecuteWork_MultipleItems_AllProcessedExactlyOnce()
    {
        using var pool = new WorkStealingPool(4);
        const int count = 1000;
        var results = new int[count];

        pool.ExecuteWork(DelegateWorkItem.Create(count, i => Interlocked.Increment(ref results[i])));

        for (int i = 0; i < count; i++)
            await Assert.That(results[i]).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteWork_MultipleConsecutiveWaves_WorksRepeatedly()
    {
        using var pool = new WorkStealingPool(3);

        for (int wave = 0; wave < 10; wave++)
        {
            const int count = 100;
            var sum = 0;
            pool.ExecuteWork(DelegateWorkItem.Create(count, i => Interlocked.Add(ref sum, i)));

            int expected = count * (count - 1) / 2;
            await Assert.That(sum).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task ExecuteWork_MoreWorkersThanItems_NoErrors()
    {
        using var pool = new WorkStealingPool(8);
        const int count = 3;
        var results = new int[count];

        pool.ExecuteWork(DelegateWorkItem.Create(count, i => Interlocked.Increment(ref results[i])));

        for (int i = 0; i < count; i++)
            await Assert.That(results[i]).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteWork_StressTest_LargeItemCount()
    {
        using var pool = new WorkStealingPool();
        const int count = 100_000;
        var sum = 0L;

        pool.ExecuteWork(DelegateWorkItem.Create(count, i => Interlocked.Add(ref sum, i)));

        long expected = (long)count * (count - 1) / 2;
        await Assert.That(sum).IsEqualTo(expected);
    }

    // ---- Worker Count ----

    [Test]
    public async Task WorkerCount_DefaultValue_IsAtLeastOne()
    {
        using var pool = new WorkStealingPool();
        await Assert.That(pool.WorkerCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task WorkerCount_ExplicitValue_ReturnsConfiguredCount()
    {
        using var pool = new WorkStealingPool(5);
        await Assert.That(pool.WorkerCount).IsEqualTo(5);
    }

    // ---- Dispose ----

    [Test]
    public async Task ExecuteWork_AfterDispose_ThrowsObjectDisposedException()
    {
        var pool = new WorkStealingPool(2);
        pool.Dispose();

        var threw = false;
        try
        {
            pool.ExecuteWork(DelegateWorkItem.Create(1, _ => { }));
        }
        catch (ObjectDisposedException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Dispose_CalledTwice_DoesNotThrow()
    {
        var pool = new WorkStealingPool(2);
        pool.Dispose();
        pool.Dispose(); // should not throw
        await Assert.That(pool.WorkerCount).IsEqualTo(2);
    }

    [Test]
    public async Task Dispose_DuringExecution_WaitsForCompletion()
    {
        using var started = new ManualResetEventSlim(false);
        var pool = new WorkStealingPool(2);
        var completed = 0;

        var workTask = Task.Run(() =>
        {
            pool.ExecuteWork(DelegateWorkItem.Create(100, i =>
            {
                if (i == 0) started.Set();
                Thread.SpinWait(1000);
                Interlocked.Increment(ref completed);
            }));
        });

        started.Wait();
        pool.Dispose();

        await Assert.That(completed).IsEqualTo(100);
    }

    // ---- Exception Safety ----

    [Test]
    public async Task ExecuteWork_SingleException_ThrowsAggregateException()
    {
        using var pool = new WorkStealingPool(2);

        var threw = false;
        try
        {
            pool.ExecuteWork(DelegateWorkItem.Create(10, i =>
            {
                if (i == 5)
                    throw new InvalidOperationException("boom");
            }));
        }
        catch (AggregateException ex)
        {
            threw = true;
            await Assert.That(ex.InnerExceptions.Count).IsEqualTo(1);
            await Assert.That(ex.InnerExceptions[0]).IsTypeOf<InvalidOperationException>();
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task ExecuteWork_MultipleExceptions_CapturesAll()
    {
        using var pool = new WorkStealingPool(2);

        var threw = false;
        try
        {
            pool.ExecuteWork(DelegateWorkItem.Create(10, i =>
            {
                if (i % 3 == 0)
                    throw new InvalidOperationException($"error-{i}");
            }));
        }
        catch (AggregateException ex)
        {
            threw = true;
            await Assert.That(ex.InnerExceptions.Count).IsEqualTo(4);
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task ExecuteWork_AfterException_PoolReusable()
    {
        using var pool = new WorkStealingPool(2);

        try
        {
            pool.ExecuteWork(DelegateWorkItem.Create(10, i =>
            {
                if (i == 0) throw new InvalidOperationException("first");
            }));
        }
        catch (AggregateException)
        {
            // expected
        }

        var sum = 0;
        pool.ExecuteWork(DelegateWorkItem.Create(100, i => Interlocked.Add(ref sum, i)));
        int expected = 100 * 99 / 2;
        await Assert.That(sum).IsEqualTo(expected);
    }

    [Test]
    public async Task ExecuteWork_AllItemsComplete_DespiteExceptions()
    {
        using var pool = new WorkStealingPool(4);
        var processedCount = 0;

        try
        {
            pool.ExecuteWork(DelegateWorkItem.Create(20, i =>
            {
                Interlocked.Increment(ref processedCount);
                if (i == 10)
                    throw new InvalidOperationException("mid-stream");
            }));
        }
        catch (AggregateException)
        {
            // expected
        }

        await Assert.That(processedCount).IsEqualTo(20);
    }

    // ---- Reentrancy Guard ----

    [Test]
    public async Task ExecuteWork_ReentrantCall_ThrowsInvalidOperationException()
    {
        using var pool = new WorkStealingPool(2);

        var threw = false;
        try
        {
            pool.ExecuteWork(DelegateWorkItem.Create(10, _ =>
            {
                pool.ExecuteWork(DelegateWorkItem.Create(5, _ => { }));
            }));
        }
        catch (AggregateException ex)
        {
            threw = ex.InnerExceptions.Any(e => e is InvalidOperationException);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    // ---- Work Stealing Behavior ----

    [Test]
    public async Task ExecuteWork_UnevenWorkload_CompletesAllItems()
    {
        // Simulate uneven work — some items take much longer
        using var pool = new WorkStealingPool(4);
        const int count = 200;
        var results = new int[count];

        pool.ExecuteWork(DelegateWorkItem.Create(count, i =>
        {
            // Items 0-9 are "heavy" — work stealing should help
            if (i < 10)
                Thread.SpinWait(10000);
            Interlocked.Increment(ref results[i]);
        }));

        for (int i = 0; i < count; i++)
            await Assert.That(results[i]).IsEqualTo(1);
    }
}
