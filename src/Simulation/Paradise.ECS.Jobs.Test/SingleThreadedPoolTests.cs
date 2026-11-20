namespace Paradise.ECS.Jobs.Test;

/// <summary>
/// Both pools on a runtime without threads (browser-wasm without threading, WASI), simulated by
/// passing <c>multithreaded: false</c>: no worker is started and the calling thread runs every item
/// with the same exactly-once and exception guarantees as the threaded path.
/// </summary>
public sealed class SingleThreadedPoolTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(7)]
    [Arguments(1000)]
    public async Task JobWorkerPool_RunsEveryIndexOnceOnTheCallingThread(int count)
    {
        using var pool = new JobWorkerPool(4, null, null, multithreaded: false);
        var caller = Environment.CurrentManagedThreadId;
        var visits = new int[count];
        var otherThread = false;

        pool.For(count, i =>
        {
            visits[i]++;
            otherThread |= Environment.CurrentManagedThreadId != caller;
        });

        await Assert.That(pool.WorkerCount).IsEqualTo(0);
        await Assert.That(otherThread).IsFalse();
        foreach (var visit in visits)
            await Assert.That(visit).IsEqualTo(1);
    }

    [Test]
    public async Task JobWorkerPool_CollectsEveryFailureAndStaysUsable()
    {
        using var pool = new JobWorkerPool(-1, null, null, multithreaded: false);
        var completed = 0;

        var failure = Assert.Throws<AggregateException>(() => pool.ExecuteWork(DelegateWorkItem.Create(50, i =>
        {
            if (i % 10 == 3) throw new InvalidOperationException($"item {i}");
            completed++;
        })));

        await Assert.That(failure.InnerExceptions.Count).IsEqualTo(5);
        await Assert.That(completed).IsEqualTo(45);

        var after = 0;
        pool.For(10, _ => after++);
        await Assert.That(after).IsEqualTo(10);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(7)]
    [Arguments(1000)]
    public async Task WorkStealingPool_RunsEveryIndexOnceOnTheCallingThread(int count)
    {
        using var pool = new WorkStealingPool(4, multithreaded: false);
        var caller = Environment.CurrentManagedThreadId;
        var visits = new int[count];
        var otherThread = false;

        pool.ExecuteWork(DelegateWorkItem.Create(count, i =>
        {
            visits[i]++;
            otherThread |= Environment.CurrentManagedThreadId != caller;
        }));

        await Assert.That(pool.WorkerCount).IsEqualTo(0);
        await Assert.That(otherThread).IsFalse();
        foreach (var visit in visits)
            await Assert.That(visit).IsEqualTo(1);
    }

    [Test]
    public async Task WorkStealingPool_CollectsEveryFailureAndStaysUsable()
    {
        using var pool = new WorkStealingPool(-1, multithreaded: false);
        var completed = 0;

        var failure = Assert.Throws<AggregateException>(() => pool.ExecuteWork(DelegateWorkItem.Create(50, i =>
        {
            if (i % 10 == 3) throw new InvalidOperationException($"item {i}");
            completed++;
        })));

        await Assert.That(failure.InnerExceptions.Count).IsEqualTo(5);
        await Assert.That(completed).IsEqualTo(45);

        var after = 0;
        pool.ExecuteWork(DelegateWorkItem.Create(10, _ => after++));
        await Assert.That(after).IsEqualTo(10);
    }
}
