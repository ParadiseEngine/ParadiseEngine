namespace Paradise.ECS.SnapshotTest;

// ============================================================================
// Singleton + [CurrentTick] definitions (snapshot codegen — see assembly
// attribute in SnapshotReadScheduleTests.cs)
// ============================================================================

/// <summary>World-level data owned by a single entity (the SimulationContext pattern).</summary>
[Component]
public partial struct SnapContext
{
    public float Value;
}

[Queryable(Singleton = true)]
[With<SnapContext>(IsReadOnly = true)]
public readonly ref partial struct SnapCtx;

[Queryable(Singleton = true)]
[With<SnapContext>]
public readonly ref partial struct SnapCtxMutable;

/// <summary>Read-only singleton, STANDARD snapshot rule: binds to the READ world (stale-by-one).</summary>
public ref partial struct SnapCtxStaleReaderSystem : IEntitySystem
{
    public SnapCtxSingleton Ctx;
    public ref SnapMarker Marker;

    public void Execute() => Marker.Observed = Ctx.SnapContext.Value;
}

/// <summary>[CurrentTick] singleton: read-only components bind to the WRITE world (fresh).</summary>
public ref partial struct SnapCtxFreshReaderSystem : IEntitySystem
{
    [CurrentTick] public SnapCtxSingleton Ctx;
    public ref SnapMarker Marker;

    public void Execute() => Marker.Observed = Ctx.SnapContext.Value;
}

/// <summary>Sole writer of the singleton's component (iterates the singleton entity itself).</summary>
public ref partial struct SnapCtxBumpSystem : IEntitySystem
{
    public ref SnapContext Ctx;

    public void Execute() => Ctx.Value += 1f;
}

/// <summary>[CurrentTick] inline fresh read: must observe SnapWriterSystem's same-tick write,
/// so the scheduler is forced to run it in a LATER wave.</summary>
public ref partial struct SnapFreshInlineReaderSystem : IEntitySystem
{
    [CurrentTick] public ref readonly SnapPosition Position;
    public ref SnapMarker Marker;

    public void Execute() => Marker.Observed = Position.X;
}

/// <summary>Chunk system reading the singleton (happy path for the chunk dispatch).</summary>
public ref partial struct SnapCtxChunkReaderSystem : IChunkSystem
{
    public SnapCtxSingleton Ctx;
    public Span<SnapMarker> Markers;

    public void ExecuteChunk()
    {
        for (int i = 0; i < Markers.Length; i++)
            Markers[i].Observed = Ctx.SnapContext.Value;
    }
}

/// <summary>World system writing through a WRITABLE singleton (binds to the write world).</summary>
public ref partial struct SnapCtxWorldWriterSystem : IWorldSystem
{
    public SnapCtxMutableSingleton Ctx;

    public void Execute() => Ctx.SnapContext.Value += 1000f;
}

// ============================================================================
// Tests
// ============================================================================

public sealed class SingletonCurrentTickSnapshotTests : IDisposable
{
    private readonly SharedWorld _shared;
    private readonly World _current;
    private readonly World _write;

    public SingletonCurrentTickSnapshotTests()
    {
        _shared = SharedWorldFactory.Create();
        _current = _shared.CreateWorld();
        _write = _shared.CreateWorld();
    }

    public void Dispose() => _shared.Dispose();

    private Entity SeedContext(float value)
    {
        var e = _current.Spawn();
        _current.AddComponent(e, new SnapContext { Value = value });
        return e;
    }

    private Entity SeedMarker()
    {
        var e = _current.Spawn();
        _current.AddComponent(e, new SnapMarker());
        return e;
    }

    // ---- Resolution happy path (classic Run(): singleton binds to the only world) ----

    [Test]
    public async Task entity_system_resolves_singleton_once_for_all_entities()
    {
        SeedContext(5f);
        Entity a = SeedMarker();
        Entity b = SeedMarker();

        using var schedule = SystemSchedule.Create(_current)
            .Add<SnapCtxStaleReaderSystem>()
            .Build<SequentialWaveScheduler>();
        schedule.Run();

        await Assert.That(_current.GetComponent<SnapMarker>(a).Observed).IsEqualTo(5f);
        await Assert.That(_current.GetComponent<SnapMarker>(b).Observed).IsEqualTo(5f);
    }

    [Test]
    public async Task chunk_system_resolves_singleton()
    {
        SeedContext(7f);
        Entity a = SeedMarker();

        using var schedule = SystemSchedule.Create(_current)
            .Add<SnapCtxChunkReaderSystem>()
            .Build<SequentialWaveScheduler>();
        schedule.Run();

        await Assert.That(_current.GetComponent<SnapMarker>(a).Observed).IsEqualTo(7f);
    }

    [Test]
    public async Task world_system_writes_through_writable_singleton()
    {
        Entity ctx = SeedContext(1f);

        using var schedule = SystemSchedule.Create(_current)
            .AddWorld<SnapCtxWorldWriterSystem>()
            .Build<SequentialWaveScheduler>();
        schedule.Run();

        await Assert.That(_current.GetComponent<SnapContext>(ctx).Value).IsEqualTo(1001f);
    }

    // ---- Cardinality enforcement (checked against the WRITE world) ----

    [Test]
    public async Task zero_matching_entities_throws_naming_queryable_and_count()
    {
        SeedMarker(); // the reader's own query matches, forcing singleton resolution

        using var schedule = SystemSchedule.Create(_current)
            .Add<SnapCtxStaleReaderSystem>()
            .Build<SequentialWaveScheduler>();

        InvalidOperationException? exception = null;
        try { schedule.Run(); }
        catch (InvalidOperationException e) { exception = e; }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.Message).Contains("Singleton queryable 'Paradise.ECS.SnapshotTest.SnapCtx'");
        await Assert.That(exception.Message).Contains("matched 0 entities");
    }

    [Test]
    public async Task two_matching_entities_throws_naming_queryable_and_count()
    {
        SeedContext(1f);
        SeedContext(2f);
        SeedMarker();

        using var schedule = SystemSchedule.Create(_current)
            .Add<SnapCtxStaleReaderSystem>()
            .Build<SequentialWaveScheduler>();

        InvalidOperationException? exception = null;
        try { schedule.Run(); }
        catch (InvalidOperationException e) { exception = e; }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.Message).Contains("Singleton queryable 'Paradise.ECS.SnapshotTest.SnapCtx'");
        await Assert.That(exception.Message).Contains("matched 2 entities");
    }

    // ---- Snapshot staleness ----

    [Test]
    public async Task readonly_singleton_observes_the_previous_tick()
    {
        Entity ctx = SeedContext(10f);
        Entity agent = SeedMarker();
        _write.CopyFrom(_current);

        // Managed pre-pass write: THIS tick's value, only in the write world.
        _write.GetComponent<SnapContext>(ctx).Value = 99f;

        using var schedule = SystemSchedule.Create(_write)
            .Add<SnapCtxStaleReaderSystem>()
            .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler());
        schedule.Run(_current);

        // Standard rule: the read-only singleton binds to the READ world → previous tick.
        await Assert.That(_write.GetComponent<SnapMarker>(agent).Observed).IsEqualTo(10f);
    }

    [Test]
    public async Task current_tick_singleton_observes_this_ticks_pre_pass_write()
    {
        Entity ctx = SeedContext(10f);
        Entity agent = SeedMarker();
        _write.CopyFrom(_current);

        _write.GetComponent<SnapContext>(ctx).Value = 99f;

        using var schedule = SystemSchedule.Create(_write)
            .Add<SnapCtxFreshReaderSystem>()
            .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler());
        schedule.Run(_current);

        // [CurrentTick]: the singleton binds to the WRITE world → same-tick value.
        await Assert.That(_write.GetComponent<SnapMarker>(agent).Observed).IsEqualTo(99f);
    }

    [Test]
    public async Task current_tick_inline_observes_this_ticks_pre_pass_write()
    {
        var agent = _current.Spawn();
        _current.AddComponent(agent, new SnapPosition { X = 10f });
        _current.AddComponent(agent, new SnapMarker());
        _write.CopyFrom(_current);

        _write.GetComponent<SnapPosition>(agent).X = 42f;

        using var schedule = SystemSchedule.Create(_write)
            .Add<SnapFreshInlineReaderSystem>()
            .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler());
        schedule.Run(_current);

        await Assert.That(_write.GetComponent<SnapMarker>(agent).Observed).IsEqualTo(42f);
    }

    // ---- Scheduling: CurrentTick readers execute AFTER the writer ----

    [Test]
    public async Task current_tick_inline_reader_lands_after_the_writer_and_sees_its_write()
    {
        var agent = _current.Spawn();
        _current.AddComponent(agent, new SnapPosition { X = 10f });
        _current.AddComponent(agent, new SnapMarker());
        _write.CopyFrom(_current);

        // Reader added FIRST: only the fresh-read edge can order it after the writer.
        using var schedule = SystemSchedule.Create(_write)
            .Add<SnapFreshInlineReaderSystem>()
            .Add<SnapWriterSystem>()
            .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler());
        schedule.Run(_current);

        // Writer bumped 10 → 11; the CurrentTick reader observed THIS tick's write.
        await Assert.That(_write.GetComponent<SnapPosition>(agent).X).IsEqualTo(11f);
        await Assert.That(_write.GetComponent<SnapMarker>(agent).Observed).IsEqualTo(11f);
    }

    [Test]
    public async Task current_tick_singleton_reader_lands_after_the_singleton_writer()
    {
        SeedContext(10f);
        Entity agent = SeedMarker();
        _write.CopyFrom(_current);

        using var schedule = SystemSchedule.Create(_write)
            .Add<SnapCtxFreshReaderSystem>()
            .Add<SnapCtxBumpSystem>()
            .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler());
        schedule.Run(_current);

        // Bump wrote 10 → 11 in the write world; the fresh reader saw it same-tick.
        await Assert.That(_write.GetComponent<SnapMarker>(agent).Observed).IsEqualTo(11f);
    }

    [Test]
    public async Task stale_singleton_reader_ignores_the_same_tick_writer()
    {
        SeedContext(10f);
        Entity agent = SeedMarker();
        _write.CopyFrom(_current);

        using var schedule = SystemSchedule.Create(_write)
            .Add<SnapCtxStaleReaderSystem>()
            .Add<SnapCtxBumpSystem>()
            .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler());
        schedule.Run(_current);

        // The stale reader binds to the READ world — it never sees the same-tick bump (10 → 11).
        await Assert.That(_write.GetComponent<SnapMarker>(agent).Observed).IsEqualTo(10f);
    }

    [Test]
    public async Task fresh_read_conflict_splits_waves_with_the_reader_last()
    {
        var readerMeta = Meta<SnapFreshInlineReaderSystem>();
        var writerMeta = Meta<SnapWriterSystem>();

        // Reader FIRST in the span — the implicit edge must still order it after the writer.
        int[][] waves = new SnapshotDagScheduler().ComputeWaves<SmallBitSet<uint>>([readerMeta, writerMeta]);

        await Assert.That(waves.Length).IsEqualTo(2);
        await Assert.That(waves[0].Length).IsEqualTo(1);
        await Assert.That(waves[0][0]).IsEqualTo(1); // writer (local index 1) first
        await Assert.That(waves[1][0]).IsEqualTo(0); // fresh reader second
    }

    private static SystemMetadata<SmallBitSet<uint>> Meta<T>()
        where T : ISystem<SmallBitSet<uint>, DefaultConfig>, allows ref struct
        => T.Metadata;
}
