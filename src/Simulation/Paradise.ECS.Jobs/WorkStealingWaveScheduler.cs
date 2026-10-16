namespace Paradise.ECS;

/// <summary>Executes work items using a <see cref="WorkStealingPool"/>.</summary>
/// <remarks>Distributes work through per-worker Chase-Lev deques; the caller owns the pool
/// and its lifetime.</remarks>
public sealed class WorkStealingWaveScheduler : IWaveScheduler
{
    private readonly WorkStealingPool _pool;

    /// <summary>Initializes a new <see cref="WorkStealingWaveScheduler"/> backed by the specified pool.</summary>
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
