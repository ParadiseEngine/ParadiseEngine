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

/// <summary>
/// Pre-built execution schedule for systems: a PURE program over systems, holding no world.
/// Every run names the world it acts on, so one schedule can drive any world of the same
/// registry — a pooled snapshot, a rewound copy, a headless replica — and nothing about the
/// schedule changes with it. The scheduling strategy is determined at build time
/// via the <see cref="IWaveScheduler"/> provided to the builder.
/// ECB playback happens once after all waves complete, so structural changes from commands
/// are NOT visible within the same run.
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

    /// <summary>
    /// Runs all systems in SNAPSHOT-READ mode: systems generated with
    /// <c>[assembly: SnapshotReadSystems]</c> bind their read-only fields
    /// (<c>ref readonly T</c> / <c>ReadOnlySpan&lt;T&gt;</c> / all-readonly composition data) to
    /// <paramref name="readWorld"/>'s corresponding chunk — typically the immutable previous-tick
    /// snapshot <paramref name="world"/> was <c>CopyFrom</c>'d from — while writable fields bind
    /// to <paramref name="world"/>. Reads then never alias in-flight writes, so with
    /// single-writer components every system can execute in one fully parallel wave (see
    /// <c>SnapshotDagScheduler</c>).
    ///
    /// CONTRACT: <paramref name="readWorld"/> must be the structural twin of
    /// <paramref name="world"/> (no structural changes since <c>CopyFrom</c> — structural ops go
    /// through the ECB, which plays back after this call, or happen before the copy). Chunks are
    /// paired by (archetype id, chunk index); a chunk with no read-world counterpart (entity
    /// spawned after the copy) falls back to reading its own write chunk. Systems from assemblies
    /// WITHOUT the codegen attribute keep classic single-world semantics regardless of this
    /// overload.
    /// </summary>
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
        // DEBUG structural-change guard: while waves execute, direct structural World calls
        // (Spawn/Despawn/Add-/RemoveComponent/…) throw — systems must use their injected
        // EntityCommandBuffer. try/finally keeps the flag exception-safe (a throwing system
        // must not wedge the world), and it is cleared BEFORE _ecbPool.PlaybackAll below so
        // playback's Spawn/structural work is not blocked.
        // ONE read per gated feature, before any wave is built. Read per system as the waves
        // were walked, a switch flipped mid-run would run some of a feature's systems and skip
        // the rest — a tick in which a gameplay feature half happened, and which half depended
        // on another thread's timing.
        TakeFeatureSnapshot();

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

        // Merge this run's per-work-item event writers into the world's event store, in schedule
        // order (deterministic). Always runs — with no writer this expires last frame's events.
        _eventPool.CommitTo(world.Events);
        _eventPool.ClearAll();
    }

    private void RunWaves(IWorld<TMask, TConfig> world, IWorld<TMask, TConfig>? readWorld)
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
