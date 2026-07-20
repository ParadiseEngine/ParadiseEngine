using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Paradise.ECS;

/// <summary>
/// Delegate matching the <c>RunChunk</c> signature for system dispatch.
/// Used by <see cref="SystemSchedule{TMask,TConfig}"/> to invoke systems without a generated switch.
/// </summary>
/// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
/// <typeparam name="TConfig">The world configuration type.</typeparam>
/// <param name="world">The world containing the entities (the WRITE world).</param>
/// <param name="chunk">The chunk handle to process.</param>
/// <param name="readChunkManager">Chunk memory source for read-only component bindings. In
/// classic execution this is <c>world.ChunkManager</c>; under <see cref="SystemSchedule{TMask,TConfig}.Run(IWorld{TMask,TConfig})"/>
/// it is the immutable READ world's manager. Only systems generated with
/// <c>[assembly: SnapshotReadSystems]</c> consume it — classic codegen ignores it.</param>
/// <param name="readChunk">The chunk in the read source corresponding to <paramref name="chunk"/>
/// (same archetype, same chunk index; identical entity slots per World.CopyFrom).</param>
/// <param name="readWorld">The immutable read world in snapshot mode, or null under classic
/// <c>Run()</c>. Consumed by snapshot-mode codegen to pair chunks outside the system's own query
/// (e.g. resolving the read-world copy of a <c>{Prefix}Singleton</c> field's entity).</param>
/// <param name="layout">The archetype layout describing component offsets (layouts live in
/// shared metadata, so one layout is valid for both worlds' chunks).</param>
/// <param name="entityCount">The number of entities in the chunk.</param>
/// <param name="commands">The entity command buffer for deferred structural changes.</param>
/// <param name="eventWriter">The per-work-item writer for emitting deferred events (see docs/system-events.md).</param>
public delegate void SystemRunChunkAction<TMask, TConfig>(
    IWorld<TMask, TConfig> world,
    ChunkHandle chunk,
    ChunkManager readChunkManager,
    ChunkHandle readChunk,
    IWorld<TMask, TConfig>? readWorld,
    ImmutableArchetypeLayout<TMask, TConfig> layout,
    int entityCount,
    EntityCommandBuffer commands,
    SystemEventWriter eventWriter)
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new();

/// <summary>
/// Delegate matching the <c>RunWorld</c> signature for whole-world system dispatch
/// (<see cref="IWorldSystem"/>): one invocation per schedule run, not per chunk.
/// </summary>
/// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
/// <typeparam name="TConfig">The world configuration type.</typeparam>
public delegate void SystemRunWorldAction<TMask, TConfig>(
    IWorld<TMask, TConfig> world,
    IWorld<TMask, TConfig>? readWorld,
    EntityCommandBuffer commands,
    SystemEventWriter eventWriter)
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new();

/// <summary>
/// Pre-built execution schedule for systems. The scheduling strategy is determined at build time
/// via the <see cref="IWaveScheduler"/> provided to the builder.
/// ECB playback happens once after all waves complete, so structural changes from commands
/// are NOT visible within the same <see cref="Run()"/> call.
/// Each work item receives its own <see cref="EntityCommandBuffer"/>, rented in schedule order
/// and played back in that same order — so structural changes are deterministic: any
/// <see cref="IWaveScheduler"/> (sequential or parallel, any thread count) produces an identical
/// world, including entity IDs.
/// </summary>
/// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
/// <typeparam name="TConfig">The world configuration type.</typeparam>
public sealed class SystemSchedule<TMask, TConfig> : IDisposable
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new()
{
    private readonly IWorld<TMask, TConfig> _world;
    private readonly ImmutableArray<ImmutableArray<int>> _waves;
    private readonly ImmutableArray<SystemRunChunkAction<TMask, TConfig>?> _dispatchers;
    private readonly ImmutableArray<SystemRunWorldAction<TMask, TConfig>?> _worldDispatchers;
    private readonly ImmutableArray<SystemMetadata<TMask>> _metadata;
    private readonly IWaveScheduler _scheduler;
    private readonly EntityCommandBufferPool _ecbPool;
    private readonly SystemEventBufferPool _eventPool;
    private readonly List<WorkItem<TMask, TConfig>> _workItems = new();

    internal SystemSchedule(
        IWorld<TMask, TConfig> world,
        ImmutableArray<ImmutableArray<int>> waves,
        ImmutableArray<SystemRunChunkAction<TMask, TConfig>?> dispatchers,
        ImmutableArray<SystemRunWorldAction<TMask, TConfig>?> worldDispatchers,
        ImmutableArray<SystemMetadata<TMask>> metadata,
        IWaveScheduler scheduler)
    {
        _world = world;
        _waves = waves;
        _dispatchers = dispatchers;
        _worldDispatchers = worldDispatchers;
        _metadata = metadata;
        _scheduler = scheduler;
        _ecbPool = new EntityCommandBufferPool();
        _eventPool = new SystemEventBufferPool();
    }

    /// <summary>
    /// Creates a schedule builder for the given world.
    /// </summary>
    /// <param name="world">The world to create a schedule for.</param>
    /// <returns>A new schedule builder.</returns>
    public static SystemScheduleBuilder<TMask, TConfig> Create(IWorld<TMask, TConfig> world)
        => new(world);

    /// <summary>
    /// Runs all enabled systems using the scheduler provided at build time.
    /// Work items are built for all waves upfront, then handed to
    /// <see cref="IWaveScheduler.Execute{TMask,TConfig}"/> for execution.
    /// ECB playback happens once after all execution completes.
    /// </summary>
    public void Run() => RunInternal(readWorld: null);

    /// <summary>
    /// Runs all systems in SNAPSHOT-READ mode: systems generated with
    /// <c>[assembly: SnapshotReadSystems]</c> bind their read-only fields
    /// (<c>ref readonly T</c> / <c>ReadOnlySpan&lt;T&gt;</c> / all-readonly composition data) to
    /// <paramref name="readWorld"/>'s corresponding chunk — typically the immutable previous-tick
    /// snapshot this world was <c>CopyFrom</c>'d — while writable fields bind to this world.
    /// Reads then never alias in-flight writes, so with single-writer components every system can
    /// execute in one fully parallel wave (see <c>SnapshotDagScheduler</c>).
    ///
    /// CONTRACT: <paramref name="readWorld"/> must be the structural twin of this schedule's world
    /// (no structural changes since <c>CopyFrom</c> — structural ops go through the ECB, which
    /// plays back after this call, or happen before the copy). Chunks are paired by
    /// (archetype id, chunk index); a chunk with no read-world counterpart (entity spawned after
    /// the copy) falls back to reading its own write chunk. Systems from assemblies WITHOUT the
    /// codegen attribute keep classic single-world semantics regardless of this overload.
    /// </summary>
    /// <param name="readWorld">The immutable world read-only fields bind to.</param>
    public void Run(IWorld<TMask, TConfig> readWorld)
    {
        ArgumentNullException.ThrowIfNull(readWorld);
        RunInternal(readWorld);
    }

    private void RunInternal(IWorld<TMask, TConfig>? readWorld)
    {
        // DEBUG structural-change guard: while waves execute, direct structural World calls
        // (Spawn/Despawn/Add-/RemoveComponent/…) throw — systems must use their injected
        // EntityCommandBuffer. try/finally keeps the flag exception-safe (a throwing system
        // must not wedge the world), and it is cleared BEFORE _ecbPool.PlaybackAll below so
        // playback's Spawn/structural work is not blocked.
        _world.SetSystemRunInProgress(true);
        try
        {
            RunWaves(readWorld);
        }
        finally
        {
            _world.SetSystemRunInProgress(false);
        }

        _ecbPool.PlaybackAll(_world);
        _ecbPool.ClearAll();

        // Merge this run's per-work-item event writers into the world's event store, in schedule
        // order (deterministic). Always runs — with no writer this expires last frame's events.
        _eventPool.CommitTo(_world.Events);
        _eventPool.ClearAll();
    }

    private void RunWaves(IWorld<TMask, TConfig>? readWorld)
    {
        // Work items are constructed on this thread in (wave, position-in-wave, chunk) order,
        // and each rents its own ECB from the pool at construction time. Rent order therefore
        // equals schedule order, and PlaybackAll replays in that same order — commands apply as
        // if the schedule had run serially, independent of the wave scheduler's threading.
        foreach (var wave in _waves)
        {
            _workItems.Clear();
            foreach (var systemId in wave)
            {
                // World systems: one work item per run, dispatched with both worlds.
                if (_worldDispatchers[systemId] is { } worldDispatcher)
                {
                    _workItems.Add(new WorkItem<TMask, TConfig>(
                        systemId, worldDispatcher, _world, readWorld, _ecbPool.Rent(), _eventPool.Rent()));
                    continue;
                }

                var dispatcher = _dispatchers[systemId]!;
                var q = _world.ArchetypeRegistry.GetOrCreateQuery(_metadata[systemId].QueryDescription);
                foreach (var ci in q.Chunks)
                {
                    SnapshotChunkPairing.Resolve(_world, readWorld, ci.Archetype.Id, ci.ChunkIndex,
                        ci.Handle, ci.EntityCount, out ChunkManager readChunkManager, out ChunkHandle readChunk);

                    _workItems.Add(new WorkItem<TMask, TConfig>(
                        systemId,
                        ci.Handle,
                        readChunkManager,
                        readChunk,
                        dispatcher,
                        _world,
                        readWorld,
                        ci.Archetype.Layout.DataPointer,
                        ci.EntityCount,
                        _ecbPool.Rent(),
                        _eventPool.Rent()));
                }
            }
            _scheduler.Execute(_workItems);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _ecbPool.Dispose();
}

/// <summary>
/// Builder for selecting which systems to include in a schedule.
/// Dependency resolution happens at <see cref="Build(IDagScheduler, IWaveScheduler)"/> time,
/// computing waves only for the systems actually added.
/// </summary>
/// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
/// <typeparam name="TConfig">The world configuration type.</typeparam>
public readonly struct SystemScheduleBuilder<TMask, TConfig>
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new()
{
    private readonly IWorld<TMask, TConfig> _world;
    private readonly List<SystemMetadata<TMask>> _metadata;
    private readonly List<SystemRunChunkAction<TMask, TConfig>?> _dispatchers;
    private readonly List<SystemRunWorldAction<TMask, TConfig>?> _worldDispatchers;

    internal SystemScheduleBuilder(IWorld<TMask, TConfig> world)
    {
        _world = world;
        _metadata = new List<SystemMetadata<TMask>>();
        _dispatchers = new List<SystemRunChunkAction<TMask, TConfig>?>();
        _worldDispatchers = new List<SystemRunWorldAction<TMask, TConfig>?>();
    }

    /// <summary>Adds a per-entity or per-chunk system to the schedule.</summary>
    /// <typeparam name="T">The system type implementing <see cref="ISystem{TMask,TConfig}"/>.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public SystemScheduleBuilder<TMask, TConfig> Add<T>()
        where T : ISystem<TMask, TConfig>, allows ref struct
    {
        _metadata.Add(T.Metadata);
        _dispatchers.Add(T.RunChunk);
        _worldDispatchers.Add(null);
        return this;
    }

    /// <summary>Adds a whole-world system (<see cref="IWorldSystem"/>) to the schedule.</summary>
    /// <typeparam name="T">The system type implementing <see cref="IWorldSystemRunner{TMask,TConfig}"/>.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public SystemScheduleBuilder<TMask, TConfig> AddWorld<T>()
        where T : IWorldSystemRunner<TMask, TConfig>, allows ref struct
    {
        _metadata.Add(T.Metadata);
        _dispatchers.Add(null);
        _worldDispatchers.Add(T.RunWorld);
        return this;
    }

    /// <summary>Builds a schedule with the default DAG scheduler and a custom wave scheduler.</summary>
    /// <typeparam name="TScheduler">The wave scheduler type implementing <see cref="IWaveScheduler"/>.</typeparam>
    /// <returns>A new <see cref="SystemSchedule{TMask,TConfig}"/>.</returns>
    public SystemSchedule<TMask, TConfig> Build<TScheduler>()
        where TScheduler : IWaveScheduler, new()
        => Build(new DefaultDagScheduler(), new TScheduler());

    /// <summary>Builds a schedule with the default DAG scheduler and a custom wave scheduler instance.</summary>
    /// <param name="scheduler">The wave scheduler strategy to use.</param>
    /// <returns>A new <see cref="SystemSchedule{TMask,TConfig}"/>.</returns>
    public SystemSchedule<TMask, TConfig> Build(IWaveScheduler scheduler)
        => Build(new DefaultDagScheduler(), scheduler);

    /// <summary>Builds a schedule with a custom DAG scheduler and wave scheduler.</summary>
    /// <param name="dag">The DAG scheduler for computing execution waves.</param>
    /// <param name="scheduler">The wave scheduler strategy to use.</param>
    /// <returns>A new <see cref="SystemSchedule{TMask,TConfig}"/>.</returns>
    public SystemSchedule<TMask, TConfig> Build(IDagScheduler dag, IWaveScheduler scheduler)
    {
        var metadataSpan = CollectionsMarshal.AsSpan(_metadata);
        var rawWaves = dag.ComputeWaves(metadataSpan);
        var wavesBuilder = ImmutableArray.CreateBuilder<ImmutableArray<int>>(rawWaves.Length);
        foreach (var wave in rawWaves)
            wavesBuilder.Add(ImmutableArray.Create(wave));
        return new SystemSchedule<TMask, TConfig>(
            _world,
            wavesBuilder.MoveToImmutable(),
            ImmutableArray.Create(_dispatchers.ToArray()),
            ImmutableArray.Create(_worldDispatchers.ToArray()),
            ImmutableArray.Create(_metadata.ToArray()),
            scheduler);
    }
}
