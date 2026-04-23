namespace Paradise.ECS;

/// <summary>
/// Executes work items in parallel using a <see cref="JobWorkerPool"/>.
/// Persistent worker threads eliminate per-wave scheduling overhead.
/// Does NOT own the pool — the caller manages pool lifetime separately.
/// </summary>
public sealed class JobWaveScheduler : IWaveScheduler
{
    private readonly JobWorkerPool _pool;

    /// <summary>
    /// Initializes a new <see cref="JobWaveScheduler"/> backed by the specified worker pool.
    /// </summary>
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
