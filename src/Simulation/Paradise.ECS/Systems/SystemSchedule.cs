using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Paradise.Features;

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
/// (e.g. resolving the read-world copy of a <c>TQueryable.Singleton</c> field's entity).</param>
/// <param name="layout">The archetype layout describing component offsets (layouts live in
/// shared metadata, so one layout is valid for both worlds' chunks).</param>
/// <param name="entityCount">The number of entities in the chunk.</param>
/// <param name="commands">The entity command buffer for deferred structural changes.</param>
/// <param name="eventWriter">The per-work-item writer for emitting deferred events.</param>
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

/// <summary>A reusable execution schedule for worlds sharing a registry.</summary>
/// <remarks>
/// The builder selects the wave scheduler. Each work item owns a command buffer, rented and replayed
/// in schedule order after all waves finish. Structural changes become visible after the run, and
/// sequential or parallel execution produces identical worlds and entity IDs.
/// </remarks>
/// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
/// <typeparam name="TConfig">The world configuration type.</typeparam>
public sealed class SystemSchedule<TMask, TConfig> : IDisposable
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new()
{
    private readonly ImmutableArray<ImmutableArray<int>> _waves;
    private readonly ImmutableArray<SystemRunChunkAction<TMask, TConfig>?> _dispatchers;
    private readonly ImmutableArray<SystemRunWorldAction<TMask, TConfig>?> _worldDispatchers;
    private readonly ImmutableArray<SystemMetadata<TMask>> _metadata;
    private readonly ImmutableArray<FeatureId> _features;
    private readonly IFeatureSwitches? _switches;
    /// <summary>Whether each system runs in THIS run, read from the switchboard once before any
    /// wave is built. Sized at construction, refilled per run, never reallocated.</summary>
    private readonly bool[] _enabledThisRun;
    private readonly IWaveScheduler _scheduler;
    private readonly EntityCommandBufferPool _ecbPool;
    private readonly SystemEventBufferPool _eventPool;
    private readonly List<WorkItem<TMask, TConfig>> _workItems = new();

    internal SystemSchedule(
        ImmutableArray<ImmutableArray<int>> waves,
        ImmutableArray<SystemRunChunkAction<TMask, TConfig>?> dispatchers,
        ImmutableArray<SystemRunWorldAction<TMask, TConfig>?> worldDispatchers,
        ImmutableArray<SystemMetadata<TMask>> metadata,
        ImmutableArray<FeatureId> features,
        IFeatureSwitches? switches,
        IWaveScheduler scheduler)
    {
        _waves = waves;
        _dispatchers = dispatchers;
        _worldDispatchers = worldDispatchers;
        _metadata = metadata;
        _features = features;
        _switches = switches;
        _enabledThisRun = new bool[metadata.Length];
        _scheduler = scheduler;
        _ecbPool = new EntityCommandBufferPool();
        _eventPool = new SystemEventBufferPool();
    }

    /// <summary>
    /// Creates a schedule builder. The schedule it builds holds no world: name one on every
    /// run — <see cref="Run(IWorld{TMask,TConfig})"/> for a classic run,
    /// <see cref="Run(IWorld{TMask,TConfig}, IWorld{TMask,TConfig})"/> for snapshot-read mode.
    /// </summary>
    /// <returns>A new schedule builder.</returns>
    public static SystemScheduleBuilder<TMask, TConfig> Create() => new();

    /// <summary>
    /// Runs all enabled systems against <paramref name="world"/> using the scheduler provided
    /// at build time. Work items are built for all waves upfront, then handed to
    /// <see cref="IWaveScheduler.Execute{TMask,TConfig}"/> for execution. ECB playback happens
    /// once after all execution completes.
    /// </summary>
    /// <param name="world">The world the systems run against.</param>
    public void Run(IWorld<TMask, TConfig> world)
    {
        ArgumentNullException.ThrowIfNull(world);
        RunInternal(world, readWorld: null);
    }

    /// <summary>Runs generated snapshot systems against paired write and immutable read worlds.</summary>
    /// <remarks>
    /// The worlds must be structural twins from <c>CopyFrom</c>; defer structural changes through command
    /// buffers. Chunks pair by archetype ID and chunk index, falling back to the write chunk when no read
    /// counterpart exists. <c>[assembly: SnapshotReadSystems]</c> routes read-only fields to the read world;
    /// writes and <c>[CurrentTick]</c> reads use the write world. Unmarked assemblies retain single-world behavior.
    /// </remarks>
    /// <param name="world">The write world the systems mutate.</param>
    /// <param name="readWorld">The immutable world read-only fields bind to.</param>
    public void Run(IWorld<TMask, TConfig> world, IWorld<TMask, TConfig> readWorld)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(readWorld);
        RunInternal(world, readWorld);
    }

    private void RunInternal(IWorld<TMask, TConfig> world, IWorld<TMask, TConfig>? readWorld)
    {
        // Freeze feature gates for the whole run so a concurrent toggle cannot split a feature's systems.
        TakeFeatureSnapshot();

        // Structural mutations use command buffers while workers run; release the guard before playback.
        world.SetSystemRunInProgress(true);
        try
        {
            RunWaves(world, readWorld);
        }
        finally
        {
            world.SetSystemRunInProgress(false);
        }

        _ecbPool.PlaybackAll(world);
        _ecbPool.ClearAll();

        // Commit even with no writers so last tick's events expire.
        _eventPool.CommitTo(world.Events);
        _eventPool.ClearAll();
    }

    private void RunWaves(IWorld<TMask, TConfig> world, IWorld<TMask, TConfig>? readWorld)
    {
        // Rent on the schedule thread in (wave, system, chunk) order for deterministic playback.
        foreach (var wave in _waves)
        {
            _workItems.Clear();
            foreach (var systemId in wave)
            {
                if (!IsEnabled(systemId)) continue;

                // World systems: one work item per run, dispatched with both worlds.
                if (_worldDispatchers[systemId] is { } worldDispatcher)
                {
                    _workItems.Add(new WorkItem<TMask, TConfig>(
                        systemId, worldDispatcher, world, readWorld, _ecbPool.Rent(), _eventPool.Rent()));
                    continue;
                }

                var dispatcher = _dispatchers[systemId]!;
                var q = world.ArchetypeRegistry.GetOrCreateQuery(_metadata[systemId].QueryDescription);
                foreach (var ci in q.Chunks)
                {
                    SnapshotChunkPairing.Resolve(world, readWorld, ci.Archetype.Id, ci.ChunkIndex,
                        ci.Handle, ci.EntityCount, out ChunkManager readChunkManager, out ChunkHandle readChunk);

                    _workItems.Add(new WorkItem<TMask, TConfig>(
                        systemId,
                        ci.Handle,
                        readChunkManager,
                        readChunk,
                        dispatcher,
                        world,
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

    /// <summary>Fixes which systems run, on the scheduling thread, before the run starts. A
    /// gated system that is off then contributes no work items at all, so it costs nothing and
    /// rents no command buffer; rent order over the systems that DO run is still schedule order,
    /// so playback stays deterministic.</summary>
    private void TakeFeatureSnapshot()
    {
        if (_switches is null)
        {
            Array.Fill(_enabledThisRun, true);
            return;
        }
        for (var systemId = 0; systemId < _enabledThisRun.Length; systemId++)
        {
            var feature = _features[systemId];
            _enabledThisRun[systemId] = feature.IsEmpty || _switches.IsEnabled(feature);
        }
    }

    /// <summary>Whether this system runs in this run, as <see cref="TakeFeatureSnapshot"/>
    /// decided before the first wave.</summary>
    private bool IsEnabled(int systemId) => _enabledThisRun[systemId];

    /// <inheritdoc/>
    public void Dispose() => _ecbPool.Dispose();
}

/// <summary>
/// Builder for selecting which systems to include in a schedule. Worlds are not part of
/// this: a built schedule names its world at every run.
/// Dependency resolution happens at <see cref="Build(IDagScheduler, IWaveScheduler, IFeatureSwitches?)"/> time,
/// computing waves only for the systems actually added.
/// </summary>
/// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
/// <typeparam name="TConfig">The world configuration type.</typeparam>
public readonly struct SystemScheduleBuilder<TMask, TConfig>
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new()
{
    private readonly List<SystemMetadata<TMask>> _metadata;
    private readonly List<SystemRunChunkAction<TMask, TConfig>?> _dispatchers;
    private readonly List<SystemRunWorldAction<TMask, TConfig>?> _worldDispatchers;
    private readonly List<FeatureId> _features;

    /// <summary>Public only because C# requires a struct's parameterless constructor to be —
    /// <see cref="SystemSchedule{TMask,TConfig}.Create()"/> is the way in, and the generated
    /// per-assembly <c>SystemSchedule.Create()</c> is the way most code says it.</summary>
    public SystemScheduleBuilder()
    {
        _metadata = new List<SystemMetadata<TMask>>();
        _dispatchers = new List<SystemRunChunkAction<TMask, TConfig>?>();
        _worldDispatchers = new List<SystemRunWorldAction<TMask, TConfig>?>();
        _features = new List<FeatureId>();
    }

    /// <summary>Adds a per-entity or per-chunk system to the schedule.</summary>
    /// <typeparam name="T">The system type implementing <see cref="ISystem{TMask,TConfig}"/>.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public SystemScheduleBuilder<TMask, TConfig> Add<T>()
        where T : ISystem<TMask, TConfig>, allows ref struct
        => Add<T>(default);

    /// <summary>Adds a per-entity or per-chunk system that belongs to <paramref name="feature"/>:
    /// the schedule skips it entirely in runs where that feature is switched off.
    ///
    /// <para>This is how a GAMEPLAY feature is switched — a set of systems, named once here, off
    /// from the same config file and the same debug panel that turn a render feature off. The
    /// alternative, an <c>if</c> at the top of every system in the set, is a thing somebody
    /// forgets in one of them and pays for in all of them.</para>
    ///
    /// <para>The gate needs a switchboard to read, which
    /// <see cref="Build(IDagScheduler, IWaveScheduler, IFeatureSwitches?)"/> takes; built without
    /// one, a gated system runs.</para></summary>
    /// <typeparam name="T">The system type implementing <see cref="ISystem{TMask,TConfig}"/>.</typeparam>
    /// <param name="feature">The feature this system belongs to.</param>
    /// <returns>This builder for chaining.</returns>
    public SystemScheduleBuilder<TMask, TConfig> Add<T>(FeatureId feature)
        where T : ISystem<TMask, TConfig>, allows ref struct
    {
        _metadata.Add(T.Metadata);
        _dispatchers.Add(T.RunChunk);
        _worldDispatchers.Add(null);
        _features.Add(feature);
        return this;
    }

    /// <summary>Adds a whole-world system (<see cref="IWorldSystem"/>) to the schedule.</summary>
    /// <typeparam name="T">The system type implementing <see cref="IWorldSystemRunner{TMask,TConfig}"/>.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public SystemScheduleBuilder<TMask, TConfig> AddWorld<T>()
        where T : IWorldSystemRunner<TMask, TConfig>, allows ref struct
        => AddWorld<T>(default);

    /// <inheritdoc cref="Add{T}(FeatureId)"/>
    /// <typeparam name="T">The system type implementing <see cref="IWorldSystemRunner{TMask,TConfig}"/>.</typeparam>
    /// <param name="feature">The feature this system belongs to.</param>
    /// <returns>This builder for chaining.</returns>
    public SystemScheduleBuilder<TMask, TConfig> AddWorld<T>(FeatureId feature)
        where T : IWorldSystemRunner<TMask, TConfig>, allows ref struct
    {
        _metadata.Add(T.Metadata);
        _dispatchers.Add(null);
        _worldDispatchers.Add(T.RunWorld);
        _features.Add(feature);
        return this;
    }

    /// <summary>Builds a schedule with the default DAG scheduler and a custom wave scheduler.</summary>
    /// <typeparam name="TScheduler">The wave scheduler type implementing <see cref="IWaveScheduler"/>.</typeparam>
    /// <returns>A new <see cref="SystemSchedule{TMask,TConfig}"/>.</returns>
    public SystemSchedule<TMask, TConfig> Build<TScheduler>()
        where TScheduler : IWaveScheduler, new()
        => Build(new DefaultDagScheduler(), new TScheduler());

    /// <inheritdoc cref="Build{TScheduler}()"/>
    /// <typeparam name="TScheduler">The wave scheduler type implementing <see cref="IWaveScheduler"/>.</typeparam>
    /// <param name="switches">The engine's feature configuration, read on every run.</param>
    /// <returns>A new <see cref="SystemSchedule{TMask,TConfig}"/>.</returns>
    public SystemSchedule<TMask, TConfig> Build<TScheduler>(IFeatureSwitches switches)
        where TScheduler : IWaveScheduler, new()
        => Build(new DefaultDagScheduler(), new TScheduler(), switches);

    /// <summary>Builds a schedule with the default DAG scheduler and a custom wave scheduler instance.</summary>
    /// <param name="scheduler">The wave scheduler strategy to use.</param>
    /// <returns>A new <see cref="SystemSchedule{TMask,TConfig}"/>.</returns>
    public SystemSchedule<TMask, TConfig> Build(IWaveScheduler scheduler)
        => Build(new DefaultDagScheduler(), scheduler);

    /// <inheritdoc cref="Build(IWaveScheduler)"/>
    /// <param name="scheduler">The wave scheduler strategy to use.</param>
    /// <param name="switches">The engine's feature configuration, read on every run.</param>
    /// <returns>A new <see cref="SystemSchedule{TMask,TConfig}"/>.</returns>
    public SystemSchedule<TMask, TConfig> Build(IWaveScheduler scheduler, IFeatureSwitches switches)
        => Build(new DefaultDagScheduler(), scheduler, switches);

    /// <summary>Builds a schedule with a custom DAG scheduler and wave scheduler.</summary>
    /// <param name="dag">The DAG scheduler for computing execution waves.</param>
    /// <param name="scheduler">The wave scheduler strategy to use.</param>
    /// <returns>A new <see cref="SystemSchedule{TMask,TConfig}"/>.</returns>
    /// <param name="switches">The engine's feature configuration. Read on every run, so a
    /// feature switched off between two ticks stops running at the next one; null runs every
    /// system the builder was given, gated or not.</param>
    public SystemSchedule<TMask, TConfig> Build(IDagScheduler dag, IWaveScheduler scheduler, IFeatureSwitches? switches = null)
    {
        var metadataSpan = CollectionsMarshal.AsSpan(_metadata);
        var rawWaves = dag.ComputeWaves(metadataSpan);
        var wavesBuilder = ImmutableArray.CreateBuilder<ImmutableArray<int>>(rawWaves.Length);
        foreach (var wave in rawWaves)
            wavesBuilder.Add(ImmutableArray.Create(wave));
        return new SystemSchedule<TMask, TConfig>(
            wavesBuilder.MoveToImmutable(),
            ImmutableArray.Create(_dispatchers.ToArray()),
            ImmutableArray.Create(_worldDispatchers.ToArray()),
            ImmutableArray.Create(_metadata.ToArray()),
            ImmutableArray.Create(_features.ToArray()),
            switches,
            scheduler);
    }
}
