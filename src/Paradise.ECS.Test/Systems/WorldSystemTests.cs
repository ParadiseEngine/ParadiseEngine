namespace Paradise.ECS.Test;

// ============================================================================
// World-system test definitions (IWorldSystem: one Execute per schedule run,
// whole-query flat segment access)
// ============================================================================

[Queryable]
[With<TestPosition>]
[With<TestVelocity>(IsReadOnly = true)]
public readonly ref partial struct WsMovable;

/// <summary>Adds velocity.X to position.X for EVERY matching entity in one Execute call.</summary>
public ref partial struct TestWorldSumSystem : IWorldSystem
{
    public WsMovableSegments Movable;

    public void Execute()
    {
        for (int i = 0; i < Movable.Length; i++)
        {
            Movable.TestPosition[i].X += Movable.TestVelocity[i].X;
        }
    }
}

/// <summary>Computes a global aggregate (max X) then writes it to every entity — the kind of
/// cross-entity dataflow per-entity/per-chunk systems cannot express.</summary>
public ref partial struct TestWorldMaxBroadcastSystem : IWorldSystem
{
    public WsMovableSegments Movable;

    public void Execute()
    {
        float max = float.MinValue;
        for (int i = 0; i < Movable.Length; i++)
        {
            if (Movable.TestPosition[i].X > max) max = Movable.TestPosition[i].X;
        }

        for (int i = 0; i < Movable.Length; i++)
        {
            Movable.TestPosition[i].Y = max;
        }
    }
}

/// <summary>World system recording structural changes through the ECB.</summary>
public ref partial struct TestWorldSpawnSystem : IWorldSystem
{
    public WsMovableSegments Movable;
    public EntityCommandBuffer Commands;

    public void Execute()
    {
        var spawned = Commands.Spawn();
        Commands.AddComponent(spawned, new TestVelocity { X = Movable.Length, Y = 0, Z = 0 });
    }
}

// ============================================================================
// Tests
// ============================================================================

public sealed class WorldSystemTests : IDisposable
{
    private readonly SharedWorld _sharedWorld;
    private readonly World _world;

    public WorldSystemTests()
    {
        _sharedWorld = SharedWorldFactory.Create();
        _world = _sharedWorld.CreateWorld();
    }

    public void Dispose()
    {
        _sharedWorld.Dispose();
    }

    private Entity SpawnMovable(float x, float vx)
    {
        var e = _world.Spawn();
        _world.AddComponent(e, new TestPosition { X = x, Y = 0, Z = 0 });
        _world.AddComponent(e, new TestVelocity { X = vx, Y = 0, Z = 0 });
        return e;
    }

    [Test]
    public async Task world_system_processes_every_entity_across_chunks_in_one_execute()
    {
        // Enough entities to span several chunks — segments must cross chunk boundaries.
        const int count = 2000;
        var entities = new List<Entity>(count);
        for (int i = 0; i < count; i++)
        {
            entities.Add(SpawnMovable(x: i, vx: 0.5f));
        }

        using var schedule = SystemSchedule.Create(_world)
            .AddWorld<TestWorldSumSystem>()
            .Build<SequentialWaveScheduler>();
        schedule.Run();

        for (int i = 0; i < count; i += 97) // sample across chunk boundaries
        {
            await Assert.That(_world.GetComponent<TestPosition>(entities[i]).X).IsEqualTo(i + 0.5f);
        }
        await Assert.That(_world.GetComponent<TestPosition>(entities[^1]).X).IsEqualTo(count - 1 + 0.5f);
    }

    [Test]
    public async Task world_system_expresses_global_aggregates()
    {
        var a = SpawnMovable(3f, 0f);
        var b = SpawnMovable(42f, 0f);
        var c = SpawnMovable(7f, 0f);

        using var schedule = SystemSchedule.Create(_world)
            .AddWorld<TestWorldMaxBroadcastSystem>()
            .Build<SequentialWaveScheduler>();
        schedule.Run();

        await Assert.That(_world.GetComponent<TestPosition>(a).Y).IsEqualTo(42f);
        await Assert.That(_world.GetComponent<TestPosition>(b).Y).IsEqualTo(42f);
        await Assert.That(_world.GetComponent<TestPosition>(c).Y).IsEqualTo(42f);
    }

    [Test]
    public async Task world_system_ecb_plays_back_after_the_run()
    {
        SpawnMovable(1f, 0f);
        SpawnMovable(2f, 0f);
        int before = _world.EntityCount;

        using var schedule = SystemSchedule.Create(_world)
            .AddWorld<TestWorldSpawnSystem>()
            .Build<SequentialWaveScheduler>();
        schedule.Run();

        await Assert.That(_world.EntityCount).IsEqualTo(before + 1);
    }

    [Test]
    public async Task world_and_entity_systems_mix_in_one_schedule_with_mask_ordering()
    {
        var e = SpawnMovable(10f, 1f);

        // TestMovementSystem (entity, writes TestPosition) and TestWorldSumSystem (world, writes
        // TestPosition) overlap on writes → the DAG orders them into separate waves, both run.
        using var schedule = SystemSchedule.Create(_world)
            .Add<TestMovementSystem>()
            .AddWorld<TestWorldSumSystem>()
            .Build<SequentialWaveScheduler>();
        schedule.Run();

        // Movement: +vel (1) then WorldSum: +vel (1) — or the reverse; either way +2 total.
        await Assert.That(_world.GetComponent<TestPosition>(e).X).IsEqualTo(12f);
    }

    [Test]
    public async Task world_system_write_masks_order_waves()
    {
        var movementMeta = GetMeta<TestMovementSystem>();
        var worldMeta = GetWorldMeta<TestWorldSumSystem>();
        var gravityMeta = GetMeta<TestGravitySystem>(); // writes TestVelocity only

        // Write∩write (TestPosition) → two waves even under the snapshot scheduler.
        int[][] conflicting = new SnapshotDagScheduler().ComputeWaves<SmallBitSet<uint>>([movementMeta, worldMeta]);
        await Assert.That(conflicting.Length).IsEqualTo(2);

        // Disjoint writes (TestPosition vs TestVelocity) → one shared wave.
        // (TestWorldSumSystem reads TestVelocity, but snapshot reads don't conflict.)
        int[][] disjoint = new SnapshotDagScheduler().ComputeWaves<SmallBitSet<uint>>([gravityMeta, worldMeta]);
        await Assert.That(disjoint.Length).IsEqualTo(1);
    }

    private static SystemMetadata<SmallBitSet<uint>> GetMeta<T>()
        where T : ISystem<SmallBitSet<uint>, DefaultConfig>, allows ref struct
        => T.Metadata;

    private static SystemMetadata<SmallBitSet<uint>> GetWorldMeta<T>()
        where T : IWorldSystemRunner<SmallBitSet<uint>, DefaultConfig>, allows ref struct
        => T.Metadata;
}
