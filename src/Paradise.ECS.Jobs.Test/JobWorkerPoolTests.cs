namespace Paradise.ECS.Jobs.Test;

/// <summary>
/// Tests for <see cref="JobWorkerPool"/> — persistent worker thread pool.
/// </summary>
public sealed class JobWorkerPoolTests
{
    // ---- Basic Functionality ----

    [Test]
    public async Task ExecuteWork_ZeroItems_IsNoOp()
    {
        using var pool = new JobWorkerPool(2);
        var invoked = false;
        pool.ExecuteWork(DelegateWorkItem.Create(0, _ => invoked = true));
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task ExecuteWork_SingleItem_ExecutesOnCallingThread()
    {
        using var pool = new JobWorkerPool(2);
        var callingThreadId = Environment.CurrentManagedThreadId;
        var executedThreadId = -1;

        pool.ExecuteWork(DelegateWorkItem.Create(1, _ => executedThreadId = Environment.CurrentManagedThreadId));

        await Assert.That(executedThreadId).IsEqualTo(callingThreadId);
    }

    [Test]
    public async Task ExecuteWork_MultipleItems_AllProcessedExactlyOnce()
    {
        using var pool = new JobWorkerPool(4);
        const int count = 1000;
        var results = new int[count];

        pool.ExecuteWork(DelegateWorkItem.Create(count, i => Interlocked.Increment(ref results[i])));

        for (int i = 0; i < count; i++)
            await Assert.That(results[i]).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteWork_MultipleConsecutiveWaves_WorksRepeatedly()
    {
        using var pool = new JobWorkerPool(3);

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
        using var pool = new JobWorkerPool(8);
        const int count = 3;
        var results = new int[count];

        pool.ExecuteWork(DelegateWorkItem.Create(count, i => Interlocked.Increment(ref results[i])));

        for (int i = 0; i < count; i++)
            await Assert.That(results[i]).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteWork_StressTest_LargeItemCount()
    {
        using var pool = new JobWorkerPool();
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
        using var pool = new JobWorkerPool();
        await Assert.That(pool.WorkerCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task WorkerCount_ExplicitValue_ReturnsConfiguredCount()
    {
        using var pool = new JobWorkerPool(5);
        await Assert.That(pool.WorkerCount).IsEqualTo(5);
    }

    // ---- Dispose ----

    [Test]
    public async Task ExecuteWork_AfterDispose_ThrowsObjectDisposedException()
    {
        var pool = new JobWorkerPool(2);
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
        var pool = new JobWorkerPool(2);
        pool.Dispose();
        pool.Dispose(); // should not throw
        await Assert.That(pool.WorkerCount).IsEqualTo(2);
    }

    [Test]
    public async Task Dispose_DuringExecution_WaitsForCompletion()
    {
        using var started = new ManualResetEventSlim(false);
        var pool = new JobWorkerPool(2);
        var completed = 0;

        // Start work on a background thread
        var workTask = Task.Run(() =>
        {
            pool.ExecuteWork(DelegateWorkItem.Create(100, i =>
            {
                if (i == 0) started.Set();
                Thread.SpinWait(1000);
                Interlocked.Increment(ref completed);
            }));
        });

        // Wait for work to begin, then try to dispose
        started.Wait();
        pool.Dispose();

        // Work should have completed before Dispose returned
        await Assert.That(completed).IsEqualTo(100);
    }

    // ---- Exception Safety ----

    [Test]
    public async Task ExecuteWork_SingleException_ThrowsAggregateException()
    {
        using var pool = new JobWorkerPool(2);

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
        using var pool = new JobWorkerPool(2);

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
            // Items 0, 3, 6, 9 throw → 4 exceptions
            await Assert.That(ex.InnerExceptions.Count).IsEqualTo(4);
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task ExecuteWork_AfterException_PoolReusable()
    {
        using var pool = new JobWorkerPool(2);

        // First call throws
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

        // Pool should still be usable
        var sum = 0;
        pool.ExecuteWork(DelegateWorkItem.Create(100, i => Interlocked.Add(ref sum, i)));
        int expected = 100 * 99 / 2;
        await Assert.That(sum).IsEqualTo(expected);
    }

    [Test]
    public async Task ExecuteWork_AllItemsComplete_DespiteExceptions()
    {
        using var pool = new JobWorkerPool(4);
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

        // All 20 items should have been processed even though one threw
        await Assert.That(processedCount).IsEqualTo(20);
    }

    // ---- Reentrancy Guard ----

    [Test]
    public async Task ExecuteWork_ReentrantCall_ThrowsInvalidOperationException()
    {
        using var pool = new JobWorkerPool(2);

        var threw = false;
        try
        {
            pool.ExecuteWork(DelegateWorkItem.Create(10, _ =>
            {
                // Attempt reentrant call from worker
                pool.ExecuteWork(DelegateWorkItem.Create(5, _ => { }));
            }));
        }
        catch (AggregateException ex)
        {
            // The reentrant call throws InvalidOperationException, which is captured
            threw = ex.InnerExceptions.Any(e => e is InvalidOperationException);
        }
        catch (InvalidOperationException)
        {
            // Could also be thrown directly if main thread hits it
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    // ---- Batch Claiming ----

    [Test]
    public async Task ExecuteWork_BatchClaiming_AllItemsProcessedExactlyOnce()
    {
        using var pool = new JobWorkerPool(4);
        const int count = 1000;
        var results = new int[count];

        pool.ExecuteWork(DelegateWorkItem.Create(count, i => Interlocked.Increment(ref results[i])));

        for (int i = 0; i < count; i++)
            await Assert.That(results[i]).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteWork_CountLessThanBatchSize_AllItemsProcessed()
    {
        using var pool = new JobWorkerPool(4);
        const int count = 3; // Less than batch size of 8
        var results = new int[count];

        pool.ExecuteWork(DelegateWorkItem.Create(count, i => Interlocked.Increment(ref results[i])));

        for (int i = 0; i < count; i++)
            await Assert.That(results[i]).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteWork_CountNotMultipleOfBatchSize_AllItemsProcessed()
    {
        using var pool = new JobWorkerPool(4);
        const int count = 37; // Not a multiple of batch size 8
        var results = new int[count];

        pool.ExecuteWork(DelegateWorkItem.Create(count, i => Interlocked.Increment(ref results[i])));

        for (int i = 0; i < count; i++)
            await Assert.That(results[i]).IsEqualTo(1);
    }
}
