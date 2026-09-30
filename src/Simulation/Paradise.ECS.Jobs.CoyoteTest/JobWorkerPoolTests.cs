using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;

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
