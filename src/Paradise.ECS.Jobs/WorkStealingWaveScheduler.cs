namespace Paradise.ECS;

/// <summary>
/// Executes work items using a <see cref="WorkStealingPool"/>.
/// Per-worker Chase-Lev deques provide better load balancing than
/// the atomic counter approach of <see cref="JobWaveScheduler"/>.
/// Does NOT own the pool — the caller manages pool lifetime separately.
/// </summary>
public sealed class WorkStealingWaveScheduler : IWaveScheduler
{
    private readonly WorkStealingPool _pool;

    /// <summary>
    /// Initializes a new <see cref="WorkStealingWaveScheduler"/> backed by the specified pool.
    /// </summary>
    /// <param name="pool">The work-stealing pool to dispatch work items to. Must outlive this scheduler.</param>
    public WorkStealingWaveScheduler(WorkStealingPool pool)
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
