using Microsoft.Coyote.SystematicTesting;
using Specification = Microsoft.Coyote.Specifications.Specification;

namespace Paradise.ECS.Jobs.CoyoteTest;

/// <summary>Exercises the production pool's admission, retirement and disposal protocol.</summary>
public static class JobWorkerPoolTests
{
    [Test]
    public static async Task DelayedClaim_CannotCrossWaveBoundary()
    {
        var gate = new object();
        bool staleClaim = false;
        bool releaseClaim = false;
        var itemsFinished = new TaskCompletionSource();
        var nextVisits = new int[24];
        using var pool = new JobWorkerPool(2, (start, isWorker) =>
        {
            if (!isWorker || start < 2)
                return;
            lock (gate)
            {
                if (staleClaim)
                    return;
                staleClaim = true;
                Monitor.PulseAll(gate);
                while (!releaseClaim)
                    Monitor.Wait(gate);
            }
        });

        var execution = Task.Run(() =>
        {
            pool.ExecuteWork(Create(2, i =>
            {
                lock (gate)
                {
                    while (!staleClaim)
                        Monitor.Wait(gate);
                }
                if (i == 1)
                    itemsFinished.SetResult();
            }));

            lock (gate)
            {
                Specification.Assert(releaseClaim,
                    "ExecuteWork returned with an old-wave drainer suspended before its range check.");
            }
            pool.ExecuteWork(Create(24, i => Interlocked.Increment(ref nextVisits[i])));
        });

        await itemsFinished.Task.ConfigureAwait(false);
        try
        {
            await Task.Yield();
            Specification.Assert(!execution.IsCompleted, "The old wave must join its suspended drainer.");
        }
        finally
        {
            lock (gate)
            {
                releaseClaim = true;
                Monitor.PulseAll(gate);
            }
        }
        await execution.ConfigureAwait(false);
        foreach (int visits in nextVisits)
            Specification.Assert(visits == 1, "A next-wave item did not execute exactly once.");
    }

    [Test]
    public static async Task UnequalWavesAndExceptions_CompleteExactlyOnce()
    {
        await Task.Run(() =>
        {
            using var pool = new JobWorkerPool(2);
            int[] sizes = [2, 24, 0, 1, 17, 3];
            foreach (int size in sizes)
            {
                var visits = new int[size];
                int errors = 0;
                try
                {
                    pool.ExecuteWork(Create(size, i =>
                    {
                        Interlocked.Increment(ref visits[i]);
                        if (i == 0)
                            throw new InvalidOperationException("expected");
                    }));
                }
                catch (AggregateException ex)
                {
                    errors = ex.InnerExceptions.Count;
                    Specification.Assert(ex.InnerExceptions.All(e => e.Message == "expected"),
                        "A wave invoked a cleared or incorrect adapter.");
                }

                Specification.Assert(errors == (size == 0 ? 0 : 1), "Exceptions leaked between waves.");
                foreach (int count in visits)
                    Specification.Assert(count == 1, "A wave returned before each callback completed exactly once.");
            }
        }).ConfigureAwait(false);
    }

    [Test]
    public static async Task Dispose_RacingCallback_WaitsForCompletion()
    {
        var gate = new object();
        bool releaseCallback = false;
        var started = new TaskCompletionSource();
        int completed = 0;
        using var pool = new JobWorkerPool(2);
        var execution = Task.Run(() => pool.ExecuteWork(Create(17, i =>
        {
            if (i == 0)
            {
                started.SetResult();
                lock (gate)
                {
                    while (!releaseCallback)
                        Monitor.Wait(gate);
                }
            }
            Interlocked.Increment(ref completed);
        })));

        await started.Task.ConfigureAwait(false);
        var disposal = Task.Run(() =>
        {
            pool.Dispose();
            Specification.Assert(Volatile.Read(ref completed) == 17, "Dispose returned with unfinished callbacks.");
        });
        lock (gate)
        {
            releaseCallback = true;
            Monitor.PulseAll(gate);
        }
        await Task.WhenAll(execution, disposal).ConfigureAwait(false);
    }

    [Test]
    public static async Task ConcurrentDispose_WaitsForJoinedWorkers()
    {
        var gate = new object();
        bool releaseJoin = false;
        int hookCalls = 0;
        var joinStarted = new TaskCompletionSource();
        var secondStarted = new TaskCompletionSource();
        var thirdStarted = new TaskCompletionSource();
        using var pool = new JobWorkerPool(2, null, beforeWorkersJoin: () =>
        {
            Interlocked.Increment(ref hookCalls);
            joinStarted.SetResult();
            lock (gate)
            {
                while (!releaseJoin)
                    Monitor.Wait(gate);
            }
        });

        bool DisposeAndObserveJoin()
        {
            pool.Dispose();
            lock (gate)
                return releaseJoin;
        }

        var disposals = new List<Task<bool>>();
        bool[] observedRelease;
        try
        {
            disposals.Add(Task.Run(DisposeAndObserveJoin));
            await joinStarted.Task.ConfigureAwait(false);
            disposals.Add(Task.Run(() =>
            {
                secondStarted.SetResult();
                return DisposeAndObserveJoin();
            }));
            disposals.Add(Task.Run(() =>
            {
                thirdStarted.SetResult();
                return DisposeAndObserveJoin();
            }));

            await Task.WhenAll(secondStarted.Task, thirdStarted.Task).ConfigureAwait(false);
            // Explore contenders entering Dispose while the first disposer is held.
            // Return snapshots, not this yield, detect a premature return.
            await Task.Yield();
        }
        finally
        {
            lock (gate)
            {
                releaseJoin = true;
                Monitor.PulseAll(gate);
            }
            observedRelease = await Task.WhenAll(disposals).ConfigureAwait(false);
        }

        // Assert only after cleanup: a Coyote assertion can stop the scheduler.
        // An early-return mutant leaves a false snapshot even after the hook is released.
        foreach (bool released in observedRelease)
            Specification.Assert(released, "Dispose returned before the worker-join hook was released.");
        Specification.Assert(hookCalls == 1, "Concurrent disposal must join workers only once.");
    }

    [Test]
    public static async Task BatchClaimHookExceptions_CompleteExactlyOnceAndReuse()
    {
        bool[] choices = [false, true];
        foreach (bool throwOnWorker in choices)
        {
            foreach (bool throwOutOfRange in choices)
            {
                await Task.Run(() =>
                {
                    var gate = new object();
                    bool firstWave = true;
                    bool callerClaimed = false;
                    bool workerClaimed = false;
                    int hookThrows = 0;
                    var expectedHook = new InvalidOperationException("batch claim hook");
                    var expectedItem = new InvalidOperationException("work item");
                    var visits = new int[17];
                    var nextVisits = new int[24];
                    AggregateException? error = null;
                    using (var pool = new JobWorkerPool(1, (start, isWorker) =>
                    {
                        lock (gate)
                        {
                            if (!firstWave)
                                return;
                            if (isWorker)
                                workerClaimed = true;
                            else
                                callerClaimed = true;
                            Monitor.PulseAll(gate);

                            // Neither initial batch can run until both drainers claim.
                            // This guarantees worker admission and an in-range claim on each path.
                            while (!callerClaimed || !workerClaimed)
                                Monitor.Wait(gate);

                            if (isWorker == throwOnWorker && hookThrows == 0 &&
                                (start >= visits.Length) == throwOutOfRange)
                            {
                                hookThrows++;
                                throw expectedHook;
                            }
                        }
                    }))
                    {
                        try
                        {
                            pool.ExecuteWork(Create(visits.Length, i =>
                            {
                                Interlocked.Increment(ref visits[i]);
                                if (i == visits.Length - 1)
                                    throw expectedItem;
                            }));
                        }
                        catch (AggregateException ex)
                        {
                            error = ex;
                        }

                        lock (gate)
                            firstWave = false;
                        pool.ExecuteWork(Create(nextVisits.Length, i => Interlocked.Increment(ref nextVisits[i])));
                    }

                    Specification.Assert(callerClaimed && workerClaimed && hookThrows == 1,
                        "Both drainers must be admitted and the selected claim must throw exactly once.");
                    Specification.Assert(error is not null, "A batch-claim hook failure must be aggregated.");
                    Specification.Assert(error!.InnerExceptions.Count == 2 &&
                        error.InnerExceptions.Any(e => ReferenceEquals(e, expectedHook)) &&
                        error.InnerExceptions.Any(e => ReferenceEquals(e, expectedItem)),
                        "The aggregate must contain exactly the original hook and item exceptions.");
                    foreach (int count in visits)
                        Specification.Assert(count == 1, "A hook failure skipped or repeated a claimed item.");
                    foreach (int count in nextVisits)
                        Specification.Assert(count == 1, "The pool was not reusable after a hook failure.");
                }).ConfigureAwait(false);
            }
        }
    }

    [Test]
    public static async Task Dispose_FromCallbacks_IsRejected()
    {
        await Task.Run(() =>
        {
            using var pool = new JobWorkerPool(2);
            int[] sizes = [1, 17];
            foreach (int size in sizes)
            {
                int rejected = 0;
                pool.ExecuteWork(Create(size, _ =>
                {
                    try
                    {
                        pool.Dispose();
                    }
                    catch (InvalidOperationException)
                    {
                        Interlocked.Increment(ref rejected);
                    }
                }));
                Specification.Assert(rejected == size, "Every self-disposal must fail promptly without shutting down the pool.");
            }
        }).ConfigureAwait(false);
    }

    private static WorkItem[] Create(int count, Action<int> action)
    {
        var items = new WorkItem[count];
        for (int i = 0; i < count; i++)
            items[i] = new WorkItem(i, action);
        return items;
    }

    private readonly struct WorkItem(int index, Action<int> action) : IWorkItem
    {
        public void Invoke() => action(index);
    }
}
