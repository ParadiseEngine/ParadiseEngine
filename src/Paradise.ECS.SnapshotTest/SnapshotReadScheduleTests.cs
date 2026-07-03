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

    [Test]
    public async Task snapshot_run_reads_previous_tick_and_writes_new_tick()
    {
        Entity e = SeedAgent(position: 10f);
        _write.CopyFrom(_current);

        using var schedule = SystemSchedule.Create(_write)
            .Add<SnapWriterSystem>()
            .Add<SnapReaderSystem>()
            .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler());
        schedule.Run(_current);

        // Writer mutated the WRITE world starting from the copied value…
        await Assert.That(_write.GetComponent<SnapPosition>(e).X).IsEqualTo(11f);
        // …but the reader observed the CURRENT (previous-tick) value, not the in-flight write.
        await Assert.That(_write.GetComponent<SnapMarker>(e).Observed).IsEqualTo(10f);
        // The current world is never mutated.
        await Assert.That(_current.GetComponent<SnapPosition>(e).X).IsEqualTo(10f);
        await Assert.That(_current.GetComponent<SnapMarker>(e).Observed).IsEqualTo(0f);
    }

    [Test]
    public async Task classic_run_keeps_same_world_semantics_even_with_snapshot_codegen()
    {
        Entity e = SeedAgent(position: 10f);
        _write.CopyFrom(_current);

        // Classic Run(): the read source IS the write world, and the default DAG scheduler
        // orders the RAW pair into separate waves — the reader sees this tick's write.
        using var schedule = SystemSchedule.Create(_write)
            .Add<SnapWriterSystem>()
            .Add<SnapReaderSystem>()
            .Build<SequentialWaveScheduler>();
        schedule.Run();

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

        using var schedule = SystemSchedule.Create(_write)
            .Add<SnapReaderSystem>()
            .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler());
        schedule.Run(_current);

        await Assert.That(_write.GetComponent<SnapMarker>(old).Observed).IsEqualTo(10f);      // snapshot
        await Assert.That(_write.GetComponent<SnapMarker>(newcomer).Observed).IsEqualTo(100f); // fallback
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
            using var scheduleA = SystemSchedule.Create(worldA)
                .Add<SnapWriterSystem>()
                .Add<SnapReaderSystem>()
                .Build(new SnapshotDagScheduler(), new ParallelWaveScheduler());
            using var scheduleB = SystemSchedule.Create(worldB)
                .Add<SnapWriterSystem>()
                .Add<SnapReaderSystem>()
                .Build(new SnapshotDagScheduler(), new ParallelWaveScheduler());

            World last = worldA;
            for (int tick = 0; tick < 8; tick++)
            {
                (World current, World write) = tick % 2 == 0 ? (worldA, worldB) : (worldB, worldA);
                write.CopyFrom(current);
                if (tick % 2 == 0) scheduleB.Run(current);
                else scheduleA.Run(current);
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

    private static SystemMetadata<SmallBitSet<uint>> Meta<T>()
        where T : ISystem<SmallBitSet<uint>, DefaultConfig>, allows ref struct
        => T.Metadata;
}
