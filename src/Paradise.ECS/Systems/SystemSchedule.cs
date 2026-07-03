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
/// <param name="layout">The archetype layout describing component offsets (layouts live in
/// shared metadata, so one layout is valid for both worlds' chunks).</param>
/// <param name="entityCount">The number of entities in the chunk.</param>
/// <param name="commands">The entity command buffer for deferred structural changes.</param>
public delegate void SystemRunChunkAction<TMask, TConfig>(
    IWorld<TMask, TConfig> world,
    ChunkHandle chunk,
    ChunkManager readChunkManager,
    ChunkHandle readChunk,
    ImmutableArchetypeLayout<TMask, TConfig> layout,
    int entityCount,
    EntityCommandBuffer commands)
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new();

/// <summary>
/// Pre-built execution schedule for systems. The scheduling strategy is determined at build time
/// via the <see cref="IWaveScheduler"/> provided to the builder.
/// ECB playback happens once after all waves complete, so structural changes from commands
/// are NOT visible within the same <see cref="Run()"/> call.
/// </summary>
/// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
/// <typeparam name="TConfig">The world configuration type.</typeparam>
public sealed class SystemSchedule<TMask, TConfig> : IDisposable
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new()
{
    private readonly IWorld<TMask, TConfig> _world;
    private readonly ImmutableArray<ImmutableArray<int>> _waves;
    private readonly ImmutableArray<SystemRunChunkAction<TMask, TConfig>> _dispatchers;
    private readonly ImmutableArray<SystemMetadata<TMask>> _metadata;
    private readonly IWaveScheduler _scheduler;
    private readonly EntityCommandBufferPool _ecbPool;
    private readonly List<WorkItem<TMask, TConfig>> _workItems = new();

    internal SystemSchedule(
        IWorld<TMask, TConfig> world,
        ImmutableArray<ImmutableArray<int>> waves,
        ImmutableArray<SystemRunChunkAction<TMask, TConfig>> dispatchers,
        ImmutableArray<SystemMetadata<TMask>> metadata,
        IWaveScheduler scheduler)
    {
        _world = world;
        _waves = waves;
        _dispatchers = dispatchers;
        _metadata = metadata;
        _scheduler = scheduler;
        _ecbPool = new EntityCommandBufferPool(world.EntityIdAllocator);
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
        foreach (var wave in _waves)
        {
            _workItems.Clear();
            foreach (var systemId in wave)
            {
                var dispatcher = _dispatchers[systemId];
                var q = _world.ArchetypeRegistry.GetOrCreateQuery(_metadata[systemId].QueryDescription);
                foreach (var ci in q.Chunks)
                {
                    ChunkManager readChunkManager = _world.ChunkManager;
                    ChunkHandle readChunk = ci.Handle;
                    if (readWorld is not null)
                    {
                        var readArchetype = readWorld.ArchetypeRegistry.GetById(ci.Archetype.Id);
                        if (readArchetype is not null && ci.ChunkIndex < readArchetype.ChunkCount)
                        {
                            System.Diagnostics.Debug.Assert(
                                ci.EntityCount <= ReadChunkEntityCount(readArchetype, ci.ChunkIndex),
                                "Read world diverged structurally from the write world — structural changes between CopyFrom and Run(readWorld) violate the snapshot contract.");
                            readChunkManager = readWorld.ChunkManager;
                            readChunk = readArchetype.GetChunk(ci.ChunkIndex);
                        }
                        // else: archetype/chunk born after the copy — read-only fields fall back
                        // to the write chunk (new entities have no previous-tick state).
                    }

                    _workItems.Add(new WorkItem<TMask, TConfig>(
                        systemId,
                        ci.Handle,
                        readChunkManager,
                        readChunk,
                        dispatcher,
                        _world,
                        ci.Archetype.Layout.DataPointer,
                        ci.EntityCount,
                        _ecbPool));
                }
            }
            _scheduler.Execute(_workItems);
        }

        _ecbPool.PlaybackAll(_world);
        _ecbPool.ClearAll();
    }

    private static int ReadChunkEntityCount(Archetype<TMask, TConfig> archetype, int chunkIndex)
    {
        int perChunk = archetype.Layout.EntitiesPerChunk;
        return Math.Min(perChunk, archetype.EntityCount - chunkIndex * perChunk);
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
    private readonly List<SystemRunChunkAction<TMask, TConfig>> _dispatchers;

    internal SystemScheduleBuilder(IWorld<TMask, TConfig> world)
    {
        _world = world;
        _metadata = new List<SystemMetadata<TMask>>();
        _dispatchers = new List<SystemRunChunkAction<TMask, TConfig>>();
    }

    /// <summary>Adds a system to the schedule.</summary>
    /// <typeparam name="T">The system type implementing <see cref="ISystem{TMask,TConfig}"/>.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public SystemScheduleBuilder<TMask, TConfig> Add<T>()
        where T : ISystem<TMask, TConfig>, allows ref struct
    {
        _metadata.Add(T.Metadata);
        _dispatchers.Add(T.RunChunk);
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
            ImmutableArray.Create(_metadata.ToArray()),
            scheduler);
    }
}
