namespace Paradise.ECS.Jobs.Test;

/// <summary>Regressions for wave lifetime, hook failures and concurrent disposal.</summary>
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

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task Dispose_ConcurrentCall_WaitsForJoinAndFinalState(int hookFailure)
    {
        using var releaseJoin = new ManualResetEventSlim();
        var joinStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("disposal hook");
        int hookCalls = 0;
        JobWorkerPool? pool = null;
        pool = new JobWorkerPool(2, null, beforeWorkersJoin: () =>
        {
            Interlocked.Increment(ref hookCalls);
            joinStarted.SetResult();
            releaseJoin.Wait();
            if (hookFailure == 1)
                throw expected;
            if (hookFailure == 2)
                pool!.Dispose();
        });
        var poolGate = typeof(JobWorkerPool).GetField("_gate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(pool)!;
        int secondEntered = 0;

        var firstDisposal = Task.Run(() =>
        {
            try
            {
                pool.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });
        var secondDisposer = new Thread(() =>
        {
            try
            {
                // Pre-acquire the gate so WaitSleepJoin after secondEntered can
                // only be Monitor.Wait, not contention while entering Dispose.
                lock (poolGate)
                {
                    Volatile.Write(ref secondEntered, 1);
                    pool.Dispose();
                    secondDisposed.SetResult();
                }
            }
            catch (Exception ex)
            {
                secondDisposed.SetException(ex);
            }
        })
        {
            IsBackground = true,
        };

        bool secondStarted = false;
        bool settled = false;
        bool returnedBeforeJoin = false;
        try
        {
            await joinStarted.Task.WaitAsync(s_timeout).ConfigureAwait(false);
            secondDisposer.Start();
            secondStarted = true;
            // The first disposer is held outside the pool gate. The second can
            // only return (the regression) or block in Dispose's Monitor.Wait.
            settled = SpinWait.SpinUntil(() => secondDisposed.Task.IsCompleted ||
                (Volatile.Read(ref secondEntered) != 0 &&
                    (secondDisposer.ThreadState & ThreadState.WaitSleepJoin) != 0), s_timeout);
            returnedBeforeJoin = secondDisposed.Task.IsCompleted;
        }
        finally
        {
            releaseJoin.Set();
            await firstDisposal.WaitAsync(s_timeout).ConfigureAwait(false);
            if (secondStarted)
                await secondDisposed.Task.WaitAsync(s_timeout).ConfigureAwait(false);
        }

        await Assert.That(settled).IsTrue();
        await Assert.That(returnedBeforeJoin).IsFalse();
        var error = await firstDisposal.ConfigureAwait(false);
        if (hookFailure == 0)
            await Assert.That(error).IsNull();
        else if (hookFailure == 1)
            await Assert.That(ReferenceEquals(error, expected)).IsTrue();
        else
            await Assert.That(error).IsTypeOf<InvalidOperationException>();

        await Task.Run(pool.Dispose).WaitAsync(s_timeout).ConfigureAwait(false);
        await Assert.That(hookCalls).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task ExecuteWork_BatchClaimHookThrows_AggregatesAndKeepsPoolUsable(bool throwOnWorker, bool outOfRange)
    {
        using var targetClaimed = new ManualResetEventSlim();
        var expected = new InvalidOperationException("batch claim hook");
        int hookThrew = 0;
        int firstWave = 1;
        var visits = new int[17];
        var pool = new JobWorkerPool(1, (start, isWorker) =>
        {
            if (Volatile.Read(ref firstWave) == 0)
                return;
            if (isWorker == throwOnWorker && (!outOfRange || start >= visits.Length) &&
                Interlocked.CompareExchange(ref hookThrew, 1, 0) == 0)
            {
                targetClaimed.Set();
                throw expected;
            }
            // Keep the other drainer's first batch open until the target has
            // thrown, including when its failure is on an out-of-range claim.
            if (isWorker != throwOnWorker && start == 0 && !targetClaimed.Wait(s_timeout))
                throw new TimeoutException("The target drainer did not reach its throwing claim.");
        });

        var execution = Task.Run(() =>
        {
            try
            {
                pool.ExecuteWork(DelegateWorkItem.Create(visits.Length, i => Interlocked.Increment(ref visits[i])));
                return null;
            }
            catch (AggregateException ex)
            {
                return ex;
            }
        });
        try
        {
            var error = await execution.WaitAsync(s_timeout).ConfigureAwait(false);
            await Assert.That(error).IsNotNull();
            await Assert.That(error!.InnerExceptions.Count).IsEqualTo(1);
            await Assert.That(ReferenceEquals(error.InnerExceptions[0], expected)).IsTrue();
            foreach (int count in visits)
                await Assert.That(count).IsEqualTo(1);

            Volatile.Write(ref firstWave, 0);
            await Task.Run(() => pool.ExecuteWork(DelegateWorkItem.Create(visits.Length,
                i => Interlocked.Increment(ref visits[i])))).WaitAsync(s_timeout).ConfigureAwait(false);
            foreach (int count in visits)
                await Assert.That(count).IsEqualTo(2);
        }
        finally
        {
            targetClaimed.Set();
            await execution.WaitAsync(s_timeout).ConfigureAwait(false);
            await Task.Run(pool.Dispose).WaitAsync(s_timeout).ConfigureAwait(false);
        }
    }
}
