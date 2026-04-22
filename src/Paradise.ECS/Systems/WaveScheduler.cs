namespace Paradise.ECS;

/// <summary>
/// A self-contained unit of work that can be executed by a thread pool.
/// </summary>
public interface IWorkItem
{
    /// <summary>
    /// Executes this work item.
    /// </summary>
    void Invoke();
}

/// <summary>
/// Represents a unit of work within a system execution wave.
/// Stores all invocation data inline to avoid closure allocations.
/// </summary>
/// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
/// <typeparam name="TConfig">The world configuration type.</typeparam>
public readonly struct WorkItem<TMask, TConfig> : IWorkItem
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new()
{
    /// <summary>The system that produced this work item.</summary>
    public int SystemId { get; }

    /// <summary>The chunk this work item operates on.</summary>
    public ChunkHandle Chunk { get; }

    private readonly SystemRunChunkAction<TMask, TConfig> _dispatcher;
    private readonly IWorld<TMask, TConfig> _world;
    private readonly nint _layoutPtr;
    private readonly int _entityCount;
    private readonly EntityCommandBufferPool _commandPool;

    internal WorkItem(
        int systemId,
        ChunkHandle chunk,
        SystemRunChunkAction<TMask, TConfig> dispatcher,
        IWorld<TMask, TConfig> world,
        nint layoutPtr,
        int entityCount,
        EntityCommandBufferPool commandPool)
    {
        SystemId = systemId;
        Chunk = chunk;
        _dispatcher = dispatcher;
        _world = world;
        _layoutPtr = layoutPtr;
        _entityCount = entityCount;
        _commandPool = commandPool;
    }

    /// <summary>
    /// Executes this work item, resolving the thread-local ECB at invocation time.
    /// </summary>
    public void Invoke()
    {
        _dispatcher(_world, Chunk, new ImmutableArchetypeLayout<TMask, TConfig>(_layoutPtr), _entityCount, _commandPool.Get());
    }
}

/// <summary>
/// Strategy interface for executing work items.
/// Implement this to provide custom execution strategies (e.g., job systems,
/// thread pools with data affinity, or custom work-stealing schedulers).
/// ECB playback is managed by <see cref="SystemSchedule{TMask,TConfig}"/> after execution completes.
/// </summary>
/// <remarks>
/// The <c>items</c> parameter uses <see cref="IReadOnlyList{T}"/> rather than a concrete type
/// (e.g., <c>List&lt;T&gt;</c>) or <c>Span&lt;T&gt;</c> to keep the interface decoupled from
/// implementation details. The interface dispatch overhead is amortized: <c>Execute</c> is called
/// once per wave (not per chunk), so the cost of virtual calls on <c>Count</c> and <c>this[int]</c>
/// is dwarfed by the actual system execution work within each item.
/// </remarks>
public interface IWaveScheduler
{
    /// <summary>
    /// Executes all work items for a single wave. Must complete before returning.
    /// </summary>
    /// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
    /// <typeparam name="TConfig">The world configuration type.</typeparam>
    /// <param name="items">The work items for a single wave to execute.</param>
    void Execute<TMask, TConfig>(IReadOnlyList<WorkItem<TMask, TConfig>> items)
        where TMask : unmanaged, IBitSet<TMask>
        where TConfig : IConfig, new();
}

/// <summary>
/// Executes work items sequentially on the calling thread, respecting wave boundaries.
/// Single-threaded equivalent of <see cref="ParallelWaveScheduler"/>.
/// </summary>
public sealed class SequentialWaveScheduler : IWaveScheduler
{
    /// <inheritdoc/>
    public void Execute<TMask, TConfig>(IReadOnlyList<WorkItem<TMask, TConfig>> items)
        where TMask : unmanaged, IBitSet<TMask>
        where TConfig : IConfig, new()
    {
        for (int index = 0; index < items.Count; index++)
        {
            items[index].Invoke();
        }
    }
}

/// <summary>
/// Executes work items in parallel using the .NET ThreadPool via <see cref="System.Threading.Tasks.Parallel"/>.
/// Items within the same wave run in parallel; wave boundaries are respected so
/// all items in wave N complete before wave N+1 begins.
/// </summary>
public sealed class ParallelWaveScheduler : IWaveScheduler
{
    /// <inheritdoc/>
    public void Execute<TMask, TConfig>(IReadOnlyList<WorkItem<TMask, TConfig>> items)
        where TMask : unmanaged, IBitSet<TMask>
        where TConfig : IConfig, new()
    {
        switch (items.Count)
        {
            case 0: return;
            case 1:
                items[0].Invoke();
                return;
            default:
                Parallel.For(0, items.Count, i => items[i].Invoke());
                return;
        }
    }
}
