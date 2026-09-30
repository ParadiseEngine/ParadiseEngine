namespace Paradise.ECS.Jobs.Test;

/// <summary>Regressions for wave lifetime and callback disposal.</summary>
public sealed class JobWorkerPoolQuiescenceTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExecuteWork_OutOfRangeDrainer_MustExitBeforeNextWave(bool throwInFirstWave)
    {
        using var staleClaim = new ManualResetEventSlim();
        using var releaseClaim = new ManualResetEventSlim();
        var itemsFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWaveReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int paused = 0;
        int firstWave = 1;
        var firstVisits = new int[2];
        var nextVisits = new int[24];
        var pool = new JobWorkerPool(2, (start, isWorker) =>
        {
            if (isWorker && start >= 2 && Volatile.Read(ref firstWave) != 0 &&
                Interlocked.CompareExchange(ref paused, 1, 0) == 0)
            {
                staleClaim.Set();
                releaseClaim.Wait();
            }
        });

        var execution = Task.Run(() =>
        {
            try
            {
                pool.ExecuteWork(DelegateWorkItem.Create(2, i =>
                {
                    if (!staleClaim.Wait(s_timeout))
                        throw new TimeoutException("The old-wave drainer did not claim a batch.");
                    Interlocked.Increment(ref firstVisits[i]);
                    if (i == 1)
                    {
                        itemsFinished.SetResult();
                        if (throwInFirstWave)
                            throw new InvalidOperationException("first wave");
                    }
                }));
            }
            catch (AggregateException ex) when (throwInFirstWave &&
                ex.InnerExceptions.Count == 1 && ex.InnerExceptions[0].Message == "first wave")
            {
            }

            firstWaveReturned.SetResult();
            Volatile.Write(ref firstWave, 0);
            pool.ExecuteWork(DelegateWorkItem.Create(24, i => Interlocked.Increment(ref nextVisits[i])));
        });

        try
        {
            await itemsFinished.Task.WaitAsync(s_timeout).ConfigureAwait(false);
            // All in-range work has run, but a worker is deliberately held between
            // its atomic claim and range check. Wave completion must join it.
            var completed = await Task.WhenAny(firstWaveReturned.Task, Task.Delay(100)).ConfigureAwait(false);
            await Assert.That(completed == firstWaveReturned.Task).IsFalse();
        }
        finally
        {
            releaseClaim.Set();
            await execution.WaitAsync(s_timeout).ConfigureAwait(false);
            await Task.Run(pool.Dispose).WaitAsync(s_timeout).ConfigureAwait(false);
        }

        foreach (int count in firstVisits.Concat(nextVisits))
            await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteWork_UnequalEmptyAndSingleWaves_ProcessEachItemOnce()
    {
        using var pool = new JobWorkerPool(3);
        int[] sizes = [0, 1, 2, 31, 0, 17, 1, 9, 3, 64];
        foreach (int size in sizes)
        {
            var visits = new int[size];
            pool.ExecuteWork(DelegateWorkItem.Create(size, i => Interlocked.Increment(ref visits[i])));
            foreach (int count in visits)
                await Assert.That(count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ExecuteWork_BlockedCallback_DoesNotReturnEarly()
    {
        using var releaseCallback = new ManualResetEventSlim();
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int completed = 0;
        var pool = new JobWorkerPool(2);
        var execution = Task.Run(() => pool.ExecuteWork(DelegateWorkItem.Create(24, i =>
        {
            if (i == 0)
            {
                callbackStarted.SetResult();
                releaseCallback.Wait();
            }
            Interlocked.Increment(ref completed);
        })));

        try
        {
            await callbackStarted.Task.WaitAsync(s_timeout).ConfigureAwait(false);
            var finished = await Task.WhenAny(execution, Task.Delay(100)).ConfigureAwait(false);
            await Assert.That(finished == execution).IsFalse();
        }
        finally
        {
            releaseCallback.Set();
            await execution.WaitAsync(s_timeout).ConfigureAwait(false);
            await Task.Run(pool.Dispose).WaitAsync(s_timeout).ConfigureAwait(false);
        }

        await Assert.That(completed).IsEqualTo(24);
    }

    [Test]
    [Arguments(1)]
    [Arguments(24)]
    public async Task Dispose_FromCallback_ThrowsAndLeavesPoolUsable(int itemCount)
    {
        // Do not synchronously dispose a timed-out pool: a regression would otherwise
        // hang test cleanup in the same self-disposal cycle that this test detects.
        var pool = new JobWorkerPool(2);
        var execution = Task.Run(() =>
        {
            try
            {
                pool.ExecuteWork(DelegateWorkItem.Create(itemCount, _ => pool.Dispose()));
                return null;
            }
            catch (AggregateException ex)
            {
                return ex;
            }
        });
        var error = await execution.WaitAsync(s_timeout).ConfigureAwait(false);
        try
        {
            await Assert.That(error).IsNotNull();
            await Assert.That(error!.InnerExceptions.Count).IsEqualTo(itemCount);
            foreach (var inner in error.InnerExceptions)
                await Assert.That(inner).IsTypeOf<InvalidOperationException>();

            int completed = 0;
            pool.ExecuteWork(DelegateWorkItem.Create(17, _ => Interlocked.Increment(ref completed)));
            await Assert.That(completed).IsEqualTo(17);
        }
        finally
        {
            await Task.Run(pool.Dispose).WaitAsync(s_timeout).ConfigureAwait(false);
        }
    }
}
