// This assembly opts into the snapshot codegen path: read-only system fields bind to the READ
// world passed to SystemSchedule.Run(readWorld); writable fields bind to the write world.
[assembly: Paradise.ECS.SnapshotReadSystems]

namespace Paradise.ECS.SnapshotTest;

// ============================================================================
// Components & systems (generated with snapshot-read bindings)
// ============================================================================

[Component]
public partial struct SnapPosition
{
    public float X;
}

[Component]
public partial struct SnapMarker
{
    public float Observed;
}

[Component]
public partial struct SnapContended
{
    public int Value;
}

/// <summary>Marker that puts spawn-after-copy entities into a brand-new archetype.</summary>
[Component]
public partial struct SnapExtra
{
    public int Value;
}

[Component]
public partial struct SnapArbitrarySource
{
    public Entity Target;
    public float Observed;
}

/// <summary>Sole writer of SnapPosition.</summary>
public ref partial struct SnapWriterSystem : IEntitySystem
{
    public ref SnapPosition Position;

    public void Execute() => Position.X += 1f;
}

/// <summary>Reads SnapPosition (snapshot-bound), writes SnapMarker.</summary>
public ref partial struct SnapReaderSystem : IEntitySystem
{
    public ref readonly SnapPosition Position;
    public ref SnapMarker Marker;

    public void Execute() => Marker.Observed = Position.X;
}

public ref partial struct SnapContendedWriterASystem : IEntitySystem
{
    public ref SnapContended Value;

    public void Execute() => Value.Value += 1;
}

[After<SnapContendedWriterASystem>]
public ref partial struct SnapContendedWriterBSystem : IEntitySystem
{
    public ref SnapContended Value;

    public void Execute() => Value.Value *= 2;
}

[Queryable]
[With<SnapMarker>]
[With<SnapPosition>(IsReadOnly = true)]
public readonly ref partial struct SnapObserved;

/// <summary>MIXED composition (writable SnapMarker + read-only SnapPosition) in an entity
/// system: the read-only member must bind to the snapshot even though the same composition
/// also writes — per-component binding, not per-field.</summary>
public ref partial struct SnapMixedEntityReaderSystem : IEntitySystem
{
    public SnapObservedEntity Observed;

    public void Execute() => Observed.SnapMarker.Observed = Observed.SnapPosition.X;
}

/// <summary>Same mixed composition through the chunk-system path.</summary>
public ref partial struct SnapMixedChunkReaderSystem : IChunkSystem
{
    public SnapObservedChunk Observed;

    public void ExecuteChunk()
    {
        var positions = Observed.SnapPositionSpan;
        var markers = Observed.SnapMarkerSpan;
        for (int i = 0; i < markers.Length; i++)
        {
            markers[i].Observed = positions[i].X;
        }
    }
}

/// <summary>World system: reads SnapPosition via READ-ONLY segments (snapshot-bound), writes
/// SnapMarker via writable segments — one Execute over every matching entity.</summary>
public ref partial struct SnapWorldReaderSystem : IWorldSystem
{
    public SnapObservedSegments Observed;

    public void Execute()
    {
        for (int i = 0; i < Observed.Length; i++)
        {
            Observed.SnapMarker[i].Observed = Observed.SnapPosition[i].X;
        }
    }
}

/// <summary>Snapshot-bound arbitrary-entity read: the target position comes from the read world.</summary>
public ref partial struct SnapArbitraryReaderSystem : IEntitySystem
{
    public ref SnapArbitrarySource Source;
    public EntityComponentReader<SnapPosition> TargetPosition;

    public void Execute() => Source.Observed = TargetPosition[Source.Target].X;
}

/// <summary>CurrentTick arbitrary-entity read: the target position comes from the write world.</summary>
public ref partial struct SnapArbitraryFreshReaderSystem : IEntitySystem
{
    public ref SnapArbitrarySource Source;
    [CurrentTick] public EntityComponentReader<SnapPosition> TargetPosition;

    public void Execute() => Source.Observed = TargetPosition[Source.Target].X;
}

/// <summary>Arbitrary-entity writer: always binds to the write world.</summary>
public ref partial struct SnapArbitraryWriterSystem : IEntitySystem
{
    public ref SnapArbitrarySource Source;
    public EntityComponentWriter<SnapPosition> TargetPosition;

    public void Execute() => TargetPosition.Set(Source.Target, new SnapPosition { X = 42f });
}

public ref partial struct SnapAccessorReaderGraphSystem : IEntitySystem
{
    public Entity Entity;
    public EntityComponentReader<SnapPosition> TargetPosition;

    public void Execute() => _ = TargetPosition.TryGet(Entity, out _);
}

public ref partial struct SnapAccessorFreshReaderGraphSystem : IEntitySystem
{
    public Entity Entity;
    [CurrentTick] public EntityComponentReader<SnapPosition> TargetPosition;

    public void Execute() => _ = TargetPosition.TryGet(Entity, out _);
}

public ref partial struct SnapAccessorWriterGraphSystem : IEntitySystem
{
    public Entity Entity;
    public EntityComponentWriter<SnapPosition> TargetPosition;

    public void Execute() => _ = TargetPosition.TrySet(Entity, default);
}

// ============================================================================
// Tests
// ============================================================================

public sealed class SnapshotReadScheduleTests : IDisposable
{
    private readonly SharedWorld _shared;
    private readonly World _current;
    private readonly World _write;

    public SnapshotReadScheduleTests()
    {
        _shared = SharedWorldFactory.Create();
        _current = _shared.CreateWorld();
        _write = _shared.CreateWorld();
    }

    public void Dispose() => _shared.Dispose();

    private Entity SeedAgent(float position, float marker = 0f)
    {
        var e = _current.Spawn();
        _current.AddComponent(e, new SnapPosition { X = position });
        _current.AddComponent(e, new SnapMarker { Observed = marker });
        return e;
    }

    private (Entity Target, Entity Source) SeedArbitraryPair(float position)
    {
        var target = _current.Spawn();
        _current.AddComponent(target, new SnapPosition { X = position });

        var source = _current.Spawn();
        _current.AddComponent(source, new SnapArbitrarySource { Target = target });
        return (target, source);
    }

    private static SystemMetadata<ComponentMask> GetMetadata(string typeName)
    {
        foreach (ref readonly var metadata in SystemRegistry<ComponentMask>.Metadata)
        {
            if (metadata.TypeName == typeName)
                return metadata;
        }

        throw new InvalidOperationException($"System metadata for {typeName} was not found.");
    }

    [Test]
    public async Task snapshot_run_reads_previous_tick_and_writes_new_tick()
    {
        Entity e = SeedAgent(position: 10f);
        _write.CopyFrom(_current);

        using var schedule = SystemSchedule.Create()
            .Add<SnapWriterSystem>()
            .Add<SnapReaderSystem>()
            .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler());
        schedule.Run(_write, _current);

        // Writer mutated the WRITE world starting from the copied value…
        await Assert.That(_write.GetComponent<SnapPosition>(e).X).IsEqualTo(11f);
        // …but the reader observed the CURRENT (previous-tick) value, not the in-flight write.
        await Assert.That(_write.GetComponent<SnapMarker>(e).Observed).IsEqualTo(10f);
        // The current world is never mutated.
        await Assert.That(_current.GetComponent<SnapPosition>(e).X).IsEqualTo(10f);
        await Assert.That(_current.GetComponent<SnapMarker>(e).Observed).IsEqualTo(0f);
    }

    [Test]
    public async Task arbitrary_entity_reader_binds_to_snapshot_world()
    {
        var (target, source) = SeedArbitraryPair(position: 10f);
        _write.CopyFrom(_current);
        _write.GetComponent<SnapPosition>(target).X = 11f;

        using var schedule = SystemSchedule.Create()
            .Add<SnapArbitraryReaderSystem>()
            .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler());
        schedule.Run(_write, _current);

        await Assert.That(_write.GetComponent<SnapArbitrarySource>(source).Observed).IsEqualTo(10f);
    }

    [Test]
    public async Task current_tick_arbitrary_entity_reader_binds_to_write_world()
    {
        var (target, source) = SeedArbitraryPair(position: 10f);
        _write.CopyFrom(_current);
        _write.GetComponent<SnapPosition>(target).X = 11f;

        using var schedule = SystemSchedule.Create()
            .Add<SnapArbitraryFreshReaderSystem>()
            .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler());
        schedule.Run(_write, _current);

        await Assert.That(_write.GetComponent<SnapArbitrarySource>(source).Observed).IsEqualTo(11f);
    }

    [Test]
    public async Task arbitrary_entity_writer_binds_to_write_world()
    {
        var (target, _) = SeedArbitraryPair(position: 10f);
        _write.CopyFrom(_current);

        using var schedule = SystemSchedule.Create()
            .Add<SnapArbitraryWriterSystem>()
            .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler());
        schedule.Run(_write, _current);

        await Assert.That(_write.GetComponent<SnapPosition>(target).X).IsEqualTo(42f);
        await Assert.That(_current.GetComponent<SnapPosition>(target).X).IsEqualTo(10f);
    }

    [Test]
    public async Task snapshot_dag_groups_arbitrary_entity_reader_and_writer()
    {
        var reader = GetMetadata(typeof(SnapAccessorReaderGraphSystem).FullName!);
        var writer = GetMetadata(typeof(SnapAccessorWriterGraphSystem).FullName!);

        var waves = new SnapshotDagScheduler()
            .ComputeWaves<ComponentMask>([reader, writer]);

        await Assert.That(waves.Length).IsEqualTo(1);
        await Assert.That(waves[0].Length).IsEqualTo(2);
    }

    [Test]
    public async Task snapshot_dag_orders_writer_before_current_tick_reader()
    {
        var reader = GetMetadata(typeof(SnapAccessorFreshReaderGraphSystem).FullName!);
        var writer = GetMetadata(typeof(SnapAccessorWriterGraphSystem).FullName!);

        var waves = new SnapshotDagScheduler()
            .ComputeWaves<ComponentMask>([reader, writer]);

        await Assert.That(waves.Length).IsEqualTo(2);
        await Assert.That(waves[0]).Contains(1);
        await Assert.That(waves[1]).Contains(0);
    }

    [Test]
    public async Task snapshot_dag_separates_arbitrary_entity_writers()
    {
        var firstWriter = GetMetadata(typeof(SnapAccessorWriterGraphSystem).FullName!);
        var secondWriter = GetMetadata(typeof(SnapAccessorWriterGraphSystem).FullName!);

        var waves = new SnapshotDagScheduler()
            .ComputeWaves<ComponentMask>([firstWriter, secondWriter]);

        await Assert.That(waves.Length).IsEqualTo(2);
        await Assert.That(waves[0]).Contains(0);
        await Assert.That(waves[1]).Contains(1);
    }

    [Test]
    public async Task snapshot_dag_groups_arbitrary_entity_readers()
    {
        var firstReader = GetMetadata(typeof(SnapAccessorReaderGraphSystem).FullName!);
        var secondReader = GetMetadata(typeof(SnapAccessorReaderGraphSystem).FullName!);

        var waves = new SnapshotDagScheduler()
            .ComputeWaves<ComponentMask>([firstReader, secondReader]);

        await Assert.That(waves.Length).IsEqualTo(1);
        await Assert.That(waves[0].Length).IsEqualTo(2);
    }

    [Test]
    public async Task classic_run_keeps_same_world_semantics_even_with_snapshot_codegen()
    {
        Entity e = SeedAgent(position: 10f);
        _write.CopyFrom(_current);

        // Classic Run(): the read source IS the write world, and the default DAG scheduler
        // orders the RAW pair into separate waves — the reader sees this tick's write.
        using var schedule = SystemSchedule.Create()
            .Add<SnapWriterSystem>()
            .Add<SnapReaderSystem>()
            .Build<SequentialWaveScheduler>();
        schedule.Run(_write);

        await Assert.That(_write.GetComponent<SnapPosition>(e).X).IsEqualTo(11f);
        await Assert.That(_write.GetComponent<SnapMarker>(e).Observed).IsEqualTo(11f);
    }

    [Test]
    public async Task entities_spawned_after_copy_fall_back_to_the_write_chunk()
    {
        Entity old = SeedAgent(position: 10f);
        _write.CopyFrom(_current);

        // New ARCHETYPE in the write world only (SnapExtra makes the combination unique) —
        // it has no read-world counterpart, so its reads bind to its own write chunk.
        var newcomer = _write.Spawn();
        _write.AddComponent(newcomer, new SnapPosition { X = 100f });
        _write.AddComponent(newcomer, new SnapMarker());
        _write.AddComponent(newcomer, new SnapExtra());

        using var schedule = SystemSchedule.Create()
            .Add<SnapReaderSystem>()
            .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler());
        schedule.Run(_write, _current);

        await Assert.That(_write.GetComponent<SnapMarker>(old).Observed).IsEqualTo(10f);      // snapshot
        await Assert.That(_write.GetComponent<SnapMarker>(newcomer).Observed).IsEqualTo(100f); // fallback
    }

    [Test]
    public async Task mixed_composition_entity_data_readonly_member_binds_to_the_snapshot()
    {
        // SnapWriterSystem writes SnapPosition; the mixed-composition reader writes SnapMarker.
        // Write masks are disjoint → ONE wave under SnapshotDagScheduler — sound only if the
        // reader's read-only SnapPosition view binds to the snapshot, not the write world.
        Entity e = SeedAgent(position: 10f);
        _write.CopyFrom(_current);

        using var schedule = SystemSchedule.Create()
            .Add<SnapWriterSystem>()
            .Add<SnapMixedEntityReaderSystem>()
            .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler());
        schedule.Run(_write, _current);

        await Assert.That(_write.GetComponent<SnapPosition>(e).X).IsEqualTo(11f);
        // Pre-fix, the whole mixed composition bound to the write chunk → Observed read 11.
        await Assert.That(_write.GetComponent<SnapMarker>(e).Observed).IsEqualTo(10f);
    }

    [Test]
    public async Task mixed_composition_chunk_data_readonly_span_binds_to_the_snapshot()
    {
        Entity e = SeedAgent(position: 10f);
        _write.CopyFrom(_current);

        using var schedule = SystemSchedule.Create()
            .Add<SnapWriterSystem>()
            .Add<SnapMixedChunkReaderSystem>()
            .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler());
        schedule.Run(_write, _current);

        await Assert.That(_write.GetComponent<SnapPosition>(e).X).IsEqualTo(11f);
        await Assert.That(_write.GetComponent<SnapMarker>(e).Observed).IsEqualTo(10f);
    }

    [Test]
    public async Task world_system_readonly_segments_observe_the_previous_tick()
    {
        Entity a = SeedAgent(position: 10f);
        Entity b = SeedAgent(position: 20f);
        _write.CopyFrom(_current);

        // Writer (entity system) bumps positions in the write world; the world system's
        // read-only segments are bound to the CURRENT world's paired chunks.
        using var schedule = SystemSchedule.Create()
            .Add<SnapWriterSystem>()
            .AddWorld<SnapWorldReaderSystem>()
            .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler());
        schedule.Run(_write, _current);

        await Assert.That(_write.GetComponent<SnapPosition>(a).X).IsEqualTo(11f);
        await Assert.That(_write.GetComponent<SnapPosition>(b).X).IsEqualTo(21f);
        await Assert.That(_write.GetComponent<SnapMarker>(a).Observed).IsEqualTo(10f); // snapshot
        await Assert.That(_write.GetComponent<SnapMarker>(b).Observed).IsEqualTo(20f); // snapshot
    }

    [Test]
    public async Task snapshot_dag_collapses_raw_pair_into_one_wave()
    {
        var metadata = new[] { Meta<SnapWriterSystem>(), Meta<SnapReaderSystem>() };

        int[][] snapshotWaves = new SnapshotDagScheduler().ComputeWaves<SmallBitSet<uint>>(metadata);
        int[][] defaultWaves = new DefaultDagScheduler().ComputeWaves<SmallBitSet<uint>>(metadata);

        await Assert.That(snapshotWaves.Length).IsEqualTo(1); // reads can't alias writes → parallel
        await Assert.That(snapshotWaves[0].Length).IsEqualTo(2);
        await Assert.That(defaultWaves.Length).IsEqualTo(2);  // classic RAW conflict → two waves
    }

    [Test]
    public async Task snapshot_dag_still_splits_write_write_pairs()
    {
        var metadata = new[] { Meta<SnapContendedWriterASystem>(), Meta<SnapContendedWriterBSystem>() };
        int[][] waves = new SnapshotDagScheduler().ComputeWaves<SmallBitSet<uint>>(metadata);
        await Assert.That(waves.Length).IsEqualTo(2); // write∩write (and the [After] edge) → ordered
    }

    [Test]
    public async Task parallel_snapshot_run_is_bitwise_deterministic()
    {
        var results = new List<int>[2];
        for (int pass = 0; pass < 2; pass++)
        {
            using var shared = SharedWorldFactory.Create();
            var worldA = shared.CreateWorld();
            var worldB = shared.CreateWorld();
            var entities = new List<Entity>();
            for (int i = 0; i < 500; i++)
            {
                var e = worldA.Spawn();
                worldA.AddComponent(e, new SnapPosition { X = i * 0.37f });
                worldA.AddComponent(e, new SnapMarker());
                entities.Add(e);
            }

            // Ping-pong double buffer, one schedule per world — the runner's model in miniature.
            using var scheduleA = SystemSchedule.Create()
                .Add<SnapWriterSystem>()
                .Add<SnapReaderSystem>()
                .Build(new SnapshotDagScheduler(), new ParallelWaveScheduler());
            using var scheduleB = SystemSchedule.Create()
                .Add<SnapWriterSystem>()
                .Add<SnapReaderSystem>()
                .Build(new SnapshotDagScheduler(), new ParallelWaveScheduler());

            World last = worldA;
            for (int tick = 0; tick < 8; tick++)
            {
                (World current, World write) = tick % 2 == 0 ? (worldA, worldB) : (worldB, worldA);
                write.CopyFrom(current);
                if (tick % 2 == 0) scheduleB.Run(worldB, current);
                else scheduleA.Run(worldA, current);
                last = write;
            }

            var sink = new List<int>();
            foreach (var e in entities)
            {
                sink.Add(BitConverter.SingleToInt32Bits(last.GetComponent<SnapPosition>(e).X));
                sink.Add(BitConverter.SingleToInt32Bits(last.GetComponent<SnapMarker>(e).Observed));
            }
            results[pass] = sink;
        }

        await Assert.That(results[0].SequenceEqual(results[1])).IsTrue();
    }

    // ========================================================================
    // A schedule is a PURE program over systems: it stores no world, and every run names the
    // worlds it acts on. These pin that the same schedule object can be pointed anywhere.
    // ========================================================================

    [Test]
    public async Task two_world_run_reads_the_snapshot_and_writes_the_other()
    {
        Entity e = SeedAgent(position: 10f);
        _write.CopyFrom(_current);

        using var schedule = SystemSchedule.Create()
            .Add<SnapWriterSystem>()
            .Add<SnapReaderSystem>()
            .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler());
        schedule.Run(_write, _current);

        // Identical to snapshot_run_reads_previous_tick_and_writes_new_tick, which is the
        // point: naming the world per run changes nothing about what a run means.
        await Assert.That(_write.GetComponent<SnapPosition>(e).X).IsEqualTo(11f);
        await Assert.That(_write.GetComponent<SnapMarker>(e).Observed).IsEqualTo(10f);
        await Assert.That(_current.GetComponent<SnapPosition>(e).X).IsEqualTo(10f);
        await Assert.That(_current.GetComponent<SnapMarker>(e).Observed).IsEqualTo(0f);
    }

    [Test]
    public async Task one_world_run_is_a_classic_run()
    {
        Entity e = SeedAgent(position: 10f);
        _write.CopyFrom(_current);

        using var schedule = SystemSchedule.Create()
            .Add<SnapWriterSystem>()
            .Add<SnapReaderSystem>()
            .Build<SequentialWaveScheduler>();
        schedule.Run(_write);

        // Classic semantics: the read source IS the write world, so the reader sees this
        // tick's write — same as bound Run().
        await Assert.That(_write.GetComponent<SnapPosition>(e).X).IsEqualTo(11f);
        await Assert.That(_write.GetComponent<SnapMarker>(e).Observed).IsEqualTo(11f);
    }

    [Test]
    public async Task the_same_schedule_drives_several_worlds()
    {
        // The reuse a world-free schedule exists for: one program, many worlds — a pooled
        // snapshot, a rewound copy, a headless replica. A stored world could never do this.
        Entity e = SeedAgent(position: 10f);
        var second = _shared.CreateWorld();
        _write.CopyFrom(_current);
        second.CopyFrom(_current);

        using var schedule = SystemSchedule.Create()
            .Add<SnapWriterSystem>()
            .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler());
        schedule.Run(_write, _current);
        schedule.Run(second, _current);
        schedule.Run(second, _current);

        await Assert.That(_write.GetComponent<SnapPosition>(e).X).IsEqualTo(11f);
        await Assert.That(second.GetComponent<SnapPosition>(e).X).IsEqualTo(12f);
    }

    [Test]
    public async Task run_rejects_null_worlds()
    {
        using var schedule = SystemSchedule.Create()
            .Add<SnapWriterSystem>()
            .Build<SequentialWaveScheduler>();

        await Assert.That(() => schedule.Run(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => schedule.Run(_write, null!)).Throws<ArgumentNullException>();
        await Assert.That(() => schedule.Run(null!, _current)).Throws<ArgumentNullException>();
    }

    private static SystemMetadata<SmallBitSet<uint>> Meta<T>()
        where T : ISystem<SmallBitSet<uint>, DefaultConfig>, allows ref struct
        => T.Metadata;
}
