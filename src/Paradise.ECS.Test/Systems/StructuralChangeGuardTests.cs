namespace Paradise.ECS.Test;

// ============================================================================
// Offending system: bypasses the ECB and mutates the world directly mid-run.
// Hand-implements IWorldSystemRunner (instead of relying on codegen) so the
// system body can reach the world — generated systems can only see components
// and an EntityCommandBuffer by design.
// ============================================================================

/// <summary>Illegally spawns an entity directly on the world during wave execution.</summary>
public struct DirectSpawnOffenderSystem : IWorldSystemRunner<SmallBitSet<uint>, DefaultConfig>
{
    public static int SystemId => 1_000_001;

    public static SystemMetadata<SmallBitSet<uint>> Metadata => new()
    {
        SystemId = 1_000_001,
        TypeName = nameof(DirectSpawnOffenderSystem),
    };

    public static void RunWorld(
        IWorld<SmallBitSet<uint>, DefaultConfig> world,
        IWorld<SmallBitSet<uint>, DefaultConfig>? readWorld,
        EntityCommandBuffer commands,
        SystemEventWriter eventWriter)
        => world.Spawn();
}

// ============================================================================
// Tests (DEBUG builds — the guard is [Conditional("DEBUG")] and the test
// projects build Debug by default, matching CI)
// ============================================================================

/// <summary>
/// Tests for the DEBUG-only structural-change guard: direct structural World mutations during a
/// <see cref="SystemSchedule{TMask,TConfig}"/> run must throw, while ECB playback (after the
/// flag is cleared) and structural calls outside runs keep working.
/// </summary>
public sealed class StructuralChangeGuardTests : IDisposable
{
    private readonly SharedWorld _sharedWorld;
    private readonly World _world;

    public StructuralChangeGuardTests()
    {
        _sharedWorld = SharedWorldFactory.Create();
        _world = _sharedWorld.CreateWorld();
    }

    public void Dispose()
    {
        _sharedWorld.Dispose();
    }

    [Test]
    public async Task structural_call_inside_running_system_throws_and_flag_is_cleared_afterwards()
    {
        using var schedule = SystemSchedule.Create(_world)
            .AddWorld<DirectSpawnOffenderSystem>()
            .Build<SequentialWaveScheduler>();

        await Assert.That(schedule.Run).Throws<InvalidOperationException>();

        // Exception-safe clearing (try/finally): the world must be usable again after the
        // failed run — structural calls outside a run are legal.
        var entity = _world.Spawn();
        await Assert.That(_world.IsAlive(entity)).IsTrue();
    }

    [Test]
    public async Task flag_blocks_all_structural_entry_points()
    {
        var victim = _world.Spawn();
        _world.AddComponent(victim, new TestPosition { X = 1f, Y = 2f, Z = 3f });

        _world.SetSystemRunInProgress(true);
        try
        {
            await Assert.That(() => { _world.Spawn(); }).Throws<InvalidOperationException>();
            await Assert.That(() => { _world.Despawn(victim); }).Throws<InvalidOperationException>();
            await Assert.That(() => _world.AddComponent(victim, new TestVelocity()))
                .Throws<InvalidOperationException>();
            await Assert.That(() => _world.RemoveComponent<TestPosition>(victim))
                .Throws<InvalidOperationException>();
            await Assert.That(_world.Clear).Throws<InvalidOperationException>();

            // Non-structural operations stay legal while the flag is set.
            await Assert.That(_world.IsAlive(victim)).IsTrue();
            await Assert.That(_world.HasComponent<TestPosition>(victim)).IsTrue();
            await Assert.That(_world.GetComponent<TestPosition>(victim).X).IsEqualTo(1f);
        }
        finally
        {
            _world.SetSystemRunInProgress(false);
        }

        // Clearing the flag unblocks structural changes.
        _world.RemoveComponent<TestPosition>(victim);
        await Assert.That(_world.HasComponent<TestPosition>(victim)).IsFalse();
        await Assert.That(_world.Despawn(victim)).IsTrue();
    }

    [Test]
    public async Task copy_from_is_blocked_while_flag_is_set()
    {
        using var shared = new SharedWorld<SmallBitSet<ulong>, DefaultConfig>(ComponentRegistry.Shared.TypeInfos);
        var source = shared.CreateWorld();
        var target = shared.CreateWorld();
        source.Spawn();

        target.SetSystemRunInProgress(true);
        try
        {
            await Assert.That(() => target.CopyFrom(source)).Throws<InvalidOperationException>();
        }
        finally
        {
            target.SetSystemRunInProgress(false);
        }

        target.CopyFrom(source);
        await Assert.That(target.EntityCount).IsEqualTo(1);
    }

    [Test]
    public async Task spawn_via_ecb_during_run_plays_back_and_entity_is_alive()
    {
        // TestWorldSpawnSystem records the spawn on its injected ECB — legal mid-run; playback
        // happens after the guard flag is cleared, so the entity exists once Run() returns.
        var seedEntity = _world.Spawn();
        _world.AddComponent(seedEntity, new TestPosition());
        _world.AddComponent(seedEntity, new TestVelocity());

        using var schedule = SystemSchedule.Create(_world)
            .AddWorld<TestWorldSpawnSystem>()
            .Build<SequentialWaveScheduler>();

        int countBefore = _world.EntityCount;
        schedule.Run();

        await Assert.That(_world.EntityCount).IsEqualTo(countBefore + 1);
        var spawned = _world.World.GetEntity(seedEntity.Id + 1);
        await Assert.That(_world.IsAlive(spawned)).IsTrue();
    }
}
