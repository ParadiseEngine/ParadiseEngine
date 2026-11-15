namespace Paradise.ECS;

/// <summary>Executes work items in parallel using a <see cref="JobWorkerPool"/>.</summary>
/// <remarks>The supported parallel scheduler. Reuses worker threads between waves, and the same pool can
/// run non-ECS work through <see cref="JobWorkerPool.For"/>; the caller owns the pool and its lifetime.</remarks>
public sealed class JobWaveScheduler : IWaveScheduler
{
    private readonly JobWorkerPool _pool;

    /// <summary>Initializes a new <see cref="JobWaveScheduler"/> backed by the specified worker pool.</summary>
    /// <param name="pool">The worker pool to dispatch work items to. Must outlive this scheduler.</param>
    public JobWaveScheduler(JobWorkerPool pool)
    {
        _pool = pool;
    }

    /// <inheritdoc/>
    public void Execute<TMask, TConfig>(IReadOnlyList<WorkItem<TMask, TConfig>> items)
        where TMask : unmanaged, IBitSet<TMask>
        where TConfig : IConfig, new()
    {
        _pool.ExecuteWork(items);
    }
}
