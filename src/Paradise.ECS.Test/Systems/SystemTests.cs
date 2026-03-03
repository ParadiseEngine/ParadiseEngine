namespace Paradise.ECS.Test;

// ============================================================================
// Test System Definitions
// ============================================================================

/// <summary>
/// Test system: adds velocity to position per entity, using underscore-prefixed fields.
/// Regression test for ToCamelCase stripping underscore prefix in generated constructor.
/// </summary>
#pragma warning disable IDE1006 // Naming rule violation — underscore prefix intentional for regression testing
public ref partial struct TestUnderscoreFieldSystem : IEntitySystem
{
    public ref TestPosition _Position;
    public ref readonly TestVelocity _Velocity;

    public void Execute()
    {
        _Position = new TestPosition { X = _Position.X + _Velocity.X, Y = _Position.Y + _Velocity.Y, Z = _Position.Z + _Velocity.Z };
    }
}
#pragma warning restore IDE1006

/// <summary>
/// Test system: adds velocity to position per entity.
/// </summary>
public ref partial struct TestMovementSystem : IEntitySystem
{
    public ref TestPosition Position;
    public ref readonly TestVelocity Velocity;

    public void Execute()
    {
        Position = new TestPosition { X = Position.X + Velocity.X, Y = Position.Y + Velocity.Y, Z = Position.Z + Velocity.Z };
    }
}

/// <summary>
/// Test system: multiplies velocity Y by 2.
/// </summary>
public ref partial struct TestGravitySystem : IEntitySystem
{
    public ref TestVelocity Velocity;

    public void Execute()
    {
        Velocity = new TestVelocity { X = Velocity.X, Y = Velocity.Y * 2, Z = Velocity.Z };
    }
}

/// <summary>
/// Test system: runs after TestMovementSystem.
/// </summary>
[After<TestMovementSystem>]
public ref partial struct TestBoundsSystem : IEntitySystem
{
    public ref TestPosition Position;

    public void Execute()
    {
        Position = new TestPosition
        {
            X = Math.Clamp(Position.X, 0, 100),
            Y = Math.Clamp(Position.Y, 0, 100),
            Z = Math.Clamp(Position.Z, 0, 100),
        };
    }
}

/// <summary>
/// Test chunk system: batch multiplies velocity Y by 2.
/// </summary>
public ref partial struct TestGravityBatchSystem : IChunkSystem
{
    public Span<TestVelocity> Velocities;

    public void ExecuteChunk()
    {
        for (int i = 0; i < Velocities.Length; i++)
            Velocities[i] = new TestVelocity { X = Velocities[i].X, Y = Velocities[i].Y * 2, Z = Velocities[i].Z };
    }
}

/// <summary>
/// Test system: only reads health (no writes).
/// </summary>
public ref partial struct TestReadOnlyHealthSystem : IEntitySystem
{
    public ref readonly TestHealth Health;

    public void Execute()
    {
        // Read-only system - just observes health
        _ = Health.Current;
    }
}

// ============================================================================
// ECB-Enabled System Definitions
// ============================================================================

/// <summary>
/// Test entity system that spawns a new entity per processed entity using an ECB.
/// </summary>
public ref partial struct TestSpawnOnUpdateSystem : IEntitySystem
{
    public ref readonly TestPosition Position;
    public EntityCommandBuffer Commands;

    public void Execute()
    {
        var spawned = Commands.Spawn();
        Commands.AddComponent(spawned, new TestVelocity { X = Position.X, Y = 0, Z = 0 });
    }
}

/// <summary>
/// Test entity system with an ECB field that conditionally despawns entities with health &lt;= 0.
/// Uses injected Entity handle for targeted despawn.
/// </summary>
public ref partial struct TestDespawnDeadSystem : IEntitySystem
{
    public Entity Entity;
    public ref readonly TestHealth Health;
    public EntityCommandBuffer Commands;

    public void Execute()
    {
        if (Health.Current <= 0)
            Commands.Despawn(Entity);
    }
}

/// <summary>
/// Test entity system that records its Entity handle via ECB AddComponent.
/// Used to verify entity handle correctness.
/// </summary>
public ref partial struct TestRecordEntitySystem : IEntitySystem
{
    public Entity Entity;
    public ref readonly TestHealth Health;
    public EntityCommandBuffer Commands;

    public void Execute()
    {
        Commands.AddComponent(Entity, new TestDamage { Amount = Entity.Id });
    }
}

/// <summary>
/// Test chunk system with entity span injection.
/// Used to verify ReadOnlySpan&lt;Entity&gt; injection in chunk systems.
/// </summary>
public ref partial struct TestChunkEntitySpanSystem : IChunkSystem
{
    public ReadOnlySpan<Entity> Entities;
    public ReadOnlySpan<TestHealth> Healths;
    public EntityCommandBuffer Commands;

    public void ExecuteChunk()
    {
        for (int i = 0; i < Entities.Length; i++)
        {
            if (Healths[i].Current <= 0)
                Commands.Despawn(Entities[i]);
        }
    }
}

/// <summary>
/// Test system that doubles velocity. Runs after TestSpawnOnUpdateSystem to verify
/// cross-wave playback: spawned entities from the earlier wave become visible.
/// </summary>
[After<TestSpawnOnUpdateSystem>]
public ref partial struct TestDoubleVelocityAfterSpawnSystem : IEntitySystem
{
    public ref TestVelocity Velocity;

    public void Execute()
    {
        Velocity = new TestVelocity { X = Velocity.X * 2, Y = Velocity.Y * 2, Z = Velocity.Z * 2 };
    }
}

/// <summary>
/// Test chunk system with an ECB field to verify chunk-mode ECB support.
/// </summary>
public ref partial struct TestChunkSpawnSystem : IChunkSystem
{
    public ReadOnlySpan<TestVelocity> Velocities;
    public EntityCommandBuffer Commands;

    public void ExecuteChunk()
    {
        for (int i = 0; i < Velocities.Length; i++)
        {
            var spawned = Commands.Spawn();
            Commands.AddComponent(spawned, new TestPosition { X = Velocities[i].X, Y = 0, Z = 0 });
        }
    }
}

// ============================================================================
// Tests
// ============================================================================

/// <summary>
/// Tests for the System API: scheduling, execution, and DAG ordering.
/// </summary>
public sealed class SystemTests : IDisposable
{
    private readonly SharedWorld _sharedWorld;
    private readonly World _world;

    public SystemTests()
    {
        _sharedWorld = SharedWorldFactory.Create();
        _world = _sharedWorld.CreateWorld();
    }

    public void Dispose()
    {
        _sharedWorld.Dispose();
    }

    // ---- Entity System Tests ----

    [Test]
    public async Task EntitySystem_Execute_UpdatesComponents()
    {
        var e = _world.Spawn();
        _world.AddComponent(e, new TestPosition { X = 10, Y = 20, Z = 0 });
        _world.AddComponent(e, new TestVelocity { X = 1, Y = 2, Z = 0 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestMovementSystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();

        // TestGravitySystem not in schedule, so vel is unchanged
        // But TestMovementSystem reads Velocity and writes Position
        // Due to DAG ordering, if GravitySystem were present, it would run first.
        // Here only MovementSystem runs.
        var pos = _world.GetComponent<TestPosition>(e);
        await Assert.That(pos.X).IsEqualTo(11f);
        await Assert.That(pos.Y).IsEqualTo(22f);
    }

    [Test]
    public async Task EntitySystem_MultipleEntities_ProcessesAll()
    {
        var e1 = _world.Spawn();
        _world.AddComponent(e1, new TestPosition { X = 1, Y = 0, Z = 0 });
        _world.AddComponent(e1, new TestVelocity { X = 10, Y = 0, Z = 0 });

        var e2 = _world.Spawn();
        _world.AddComponent(e2, new TestPosition { X = 2, Y = 0, Z = 0 });
        _world.AddComponent(e2, new TestVelocity { X = 20, Y = 0, Z = 0 });

        var e3 = _world.Spawn();
        _world.AddComponent(e3, new TestPosition { X = 3, Y = 0, Z = 0 });
        _world.AddComponent(e3, new TestVelocity { X = 30, Y = 0, Z = 0 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestMovementSystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();

        await Assert.That(_world.GetComponent<TestPosition>(e1).X).IsEqualTo(11f);
        await Assert.That(_world.GetComponent<TestPosition>(e2).X).IsEqualTo(22f);
        await Assert.That(_world.GetComponent<TestPosition>(e3).X).IsEqualTo(33f);
    }

    [Test]
    public async Task EntitySystem_NoMatchingEntities_DoesNotCrash()
    {
        // Spawn entity without Velocity (MovementSystem requires both Position and Velocity)
        var e = _world.Spawn();
        _world.AddComponent(e, new TestPosition { X = 10, Y = 20, Z = 0 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestMovementSystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();

        // Position should be unchanged
        var pos = _world.GetComponent<TestPosition>(e);
        await Assert.That(pos.X).IsEqualTo(10f);
    }

    // ---- Chunk System Tests ----

    [Test]
    public async Task ChunkSystem_ExecuteChunk_ProcessesBatch()
    {
        var e1 = _world.Spawn();
        _world.AddComponent(e1, new TestVelocity { X = 1, Y = 5, Z = 0 });

        var e2 = _world.Spawn();
        _world.AddComponent(e2, new TestVelocity { X = 2, Y = 10, Z = 0 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestGravityBatchSystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();

        await Assert.That(_world.GetComponent<TestVelocity>(e1).Y).IsEqualTo(10f);
        await Assert.That(_world.GetComponent<TestVelocity>(e2).Y).IsEqualTo(20f);
    }

    // ---- DAG Ordering Tests ----

    [Test]
    public async Task DagOrdering_AfterAttribute_EnforcesOrder()
    {
        // BoundsSystem has [After<TestMovementSystem>] so movement runs first, then bounds clamp
        var e = _world.Spawn();
        _world.AddComponent(e, new TestPosition { X = 90, Y = 0, Z = 0 });
        _world.AddComponent(e, new TestVelocity { X = 20, Y = 0, Z = 0 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestMovementSystem>()
            .Add<TestBoundsSystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();

        // Movement: 90 + 20 = 110, then Bounds: clamp(110, 0, 100) = 100
        var pos = _world.GetComponent<TestPosition>(e);
        await Assert.That(pos.X).IsEqualTo(100f);
    }

    [Test]
    public async Task DagOrdering_ConflictDetection_SeparatesConflictingSystems()
    {
        // GravitySystem writes Velocity, MovementSystem reads Velocity → different waves
        var e = _world.Spawn();
        _world.AddComponent(e, new TestPosition { X = 0, Y = 0, Z = 0 });
        _world.AddComponent(e, new TestVelocity { X = 0, Y = 5, Z = 0 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestGravitySystem>()
            .Add<TestMovementSystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();

        // GravitySystem doubles vel.Y: 5 → 10
        // Then MovementSystem adds vel to pos: 0 + 10 = 10
        var pos = _world.GetComponent<TestPosition>(e);
        await Assert.That(pos.Y).IsEqualTo(10f);
    }

    // ---- Schedule Tests ----

    [Test]
    public async Task Schedule_AddAll_RunsAllSystems()
    {
        var e = _world.Spawn();
        _world.AddComponent(e, new TestPosition { X = 0, Y = 0, Z = 0 });
        _world.AddComponent(e, new TestVelocity { X = 1, Y = 1, Z = 0 });

        var schedule = SystemSchedule.Create(_world)
            .AddAll()
            .Build<SequentialWaveScheduler>();

        // Should not throw
        schedule.Run();

        // Position should have changed (at least MovementSystem ran)
        var pos = _world.GetComponent<TestPosition>(e);
        await Assert.That(pos.X).IsNotEqualTo(0f);
    }

    [Test]
    public async Task Schedule_RunParallel_ProducesSameResultsAsSequential()
    {
        // Create entities in one world, run sequential; create same in another, run parallel; compare
        var e1 = _world.Spawn();
        _world.AddComponent(e1, new TestPosition { X = 10, Y = 20, Z = 0 });
        _world.AddComponent(e1, new TestVelocity { X = 1, Y = 2, Z = 0 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestMovementSystem>()
            .Add<TestGravitySystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();
        var seqPos = _world.GetComponent<TestPosition>(e1);
        var seqVel = _world.GetComponent<TestVelocity>(e1);

        // Reset
        _world.Clear();
        var e2 = _world.Spawn();
        _world.AddComponent(e2, new TestPosition { X = 10, Y = 20, Z = 0 });
        _world.AddComponent(e2, new TestVelocity { X = 1, Y = 2, Z = 0 });

        var schedule2 = SystemSchedule.Create(_world)
            .Add<TestMovementSystem>()
            .Add<TestGravitySystem>()
            .Build<ParallelWaveScheduler>();

        schedule2.Run();
        var parPos = _world.GetComponent<TestPosition>(e2);
        var parVel = _world.GetComponent<TestVelocity>(e2);

        await Assert.That(parPos.X).IsEqualTo(seqPos.X);
        await Assert.That(parPos.Y).IsEqualTo(seqPos.Y);
        await Assert.That(parVel.Y).IsEqualTo(seqVel.Y);
    }

    [Test]
    public async Task Schedule_EmptySchedule_DoesNotCrash()
    {
        var seqSchedule = SystemSchedule.Create(_world)
            .Build<SequentialWaveScheduler>();
        var parSchedule = SystemSchedule.Create(_world)
            .Build<ParallelWaveScheduler>();

        seqSchedule.Run();
        parSchedule.Run();

        // just verifying no exception thrown
        await Assert.That(_world.EntityCount).IsGreaterThanOrEqualTo(0);
    }

    // ---- Underscore Field Regression Test ----

    [Test]
    public async Task UnderscoreFieldSystem_Execute_UpdatesComponents()
    {
        var e = _world.Spawn();
        _world.AddComponent(e, new TestPosition { X = 10, Y = 20, Z = 0 });
        _world.AddComponent(e, new TestVelocity { X = 1, Y = 2, Z = 0 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestUnderscoreFieldSystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();

        var pos = _world.GetComponent<TestPosition>(e);
        await Assert.That(pos.X).IsEqualTo(11f);
        await Assert.That(pos.Y).IsEqualTo(22f);
    }

    // ---- Subset Scheduling Tests ----

    [Test]
    public async Task Schedule_SubsetOnly_ComputesWavesForAddedSystems()
    {
        // Only add MovementSystem (no GravitySystem), verify it runs correctly in isolation
        var e = _world.Spawn();
        _world.AddComponent(e, new TestPosition { X = 10, Y = 20, Z = 0 });
        _world.AddComponent(e, new TestVelocity { X = 1, Y = 2, Z = 0 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestMovementSystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();

        // Only movement ran, no gravity doubling
        var vel = _world.GetComponent<TestVelocity>(e);
        await Assert.That(vel.Y).IsEqualTo(2f);
        var pos = _world.GetComponent<TestPosition>(e);
        await Assert.That(pos.X).IsEqualTo(11f);
        await Assert.That(pos.Y).IsEqualTo(22f);
    }

    [Test]
    public async Task Schedule_SubsetWithDeps_IgnoresMissingDep()
    {
        // BoundsSystem has [After<MovementSystem>], but we only add BoundsSystem — dep is skipped
        var e = _world.Spawn();
        _world.AddComponent(e, new TestPosition { X = 200, Y = 0, Z = 0 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestBoundsSystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();

        // BoundsSystem clamps to [0,100]
        var pos = _world.GetComponent<TestPosition>(e);
        await Assert.That(pos.X).IsEqualTo(100f);
    }

    [Test]
    public async Task Schedule_BuildWithCustomDagScheduler_Works()
    {
        var e = _world.Spawn();
        _world.AddComponent(e, new TestPosition { X = 0, Y = 0, Z = 0 });
        _world.AddComponent(e, new TestVelocity { X = 5, Y = 5, Z = 0 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestMovementSystem>()
            .Build(new DefaultDagScheduler(), new SequentialWaveScheduler());

        schedule.Run();

        var pos = _world.GetComponent<TestPosition>(e);
        await Assert.That(pos.X).IsEqualTo(5f);
        await Assert.That(pos.Y).IsEqualTo(5f);
    }

    // ---- SystemRegistry Tests ----

    [Test]
    public async Task SystemRegistry_Count_ReturnsCorrectValue()
    {
        await Assert.That(SystemRegistry.Count).IsGreaterThanOrEqualTo(5);
    }

    [Test]
    public async Task SystemRegistry_Metadata_HasCorrectSystemNames()
    {
        var metadata = SystemRegistry.Metadata;
        var names = new List<string>();
        for (int i = 0; i < metadata.Length; i++)
            names.Add(metadata[i].TypeName);

        await Assert.That(names).Contains("Paradise.ECS.Test.TestMovementSystem");
        await Assert.That(names).Contains("Paradise.ECS.Test.TestGravitySystem");
    }

    [Test]
    public async Task SystemRegistry_Metadata_HasAfterSystemIds()
    {
        // TestBoundsSystem has [After<TestMovementSystem>]
        var metadata = SystemRegistry.Metadata;
        var boundsAfterIds = System.Collections.Immutable.ImmutableArray<int>.Empty;
        var movementId = -1;
        var found = false;
        for (int i = 0; i < metadata.Length; i++)
        {
            if (metadata[i].TypeName == "Paradise.ECS.Test.TestBoundsSystem")
            {
                boundsAfterIds = metadata[i].AfterSystemIds;
                found = true;
            }
            if (metadata[i].TypeName == "Paradise.ECS.Test.TestMovementSystem")
                movementId = metadata[i].SystemId;
        }

        await Assert.That(found).IsTrue();
        await Assert.That(movementId).IsGreaterThanOrEqualTo(0);
        await Assert.That(boundsAfterIds).Contains(movementId);
    }

    // ---- ECB Integration Tests ----

    [Test]
    public async Task ECB_SpawnOnUpdate_SpawnsEntitiesAfterRun()
    {
        // Arrange: two entities with Position+Velocity (matches TestSpawnOnUpdateSystem)
        var e1 = _world.Spawn();
        _world.AddComponent(e1, new TestPosition { X = 10, Y = 0, Z = 0 });
        _world.AddComponent(e1, new TestVelocity());

        var e2 = _world.Spawn();
        _world.AddComponent(e2, new TestPosition { X = 20, Y = 0, Z = 0 });
        _world.AddComponent(e2, new TestVelocity());

        // TestSpawnOnUpdateSystem only queries Position, but we add Velocity so
        // the existing TestMovementSystem doesn't interfere if AddAll is used.
        var schedule = SystemSchedule.Create(_world)
            .Add<TestSpawnOnUpdateSystem>()
            .Build<SequentialWaveScheduler>();

        int countBefore = _world.EntityCount;

        // Act
        schedule.Run();

        // Assert: two new entities spawned (one per original entity)
        await Assert.That(_world.EntityCount).IsEqualTo(countBefore + 2);
    }

    [Test]
    public async Task ECB_SpawnOnUpdate_SpawnedEntitiesHaveCorrectComponents()
    {
        var e1 = _world.Spawn();
        _world.AddComponent(e1, new TestPosition { X = 42, Y = 0, Z = 0 });
        _world.AddComponent(e1, new TestVelocity());

        var schedule = SystemSchedule.Create(_world)
            .Add<TestSpawnOnUpdateSystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();

        // The spawned entity has TestVelocity with X = 42 (copied from Position.X)
        // Find it by querying all entities — spawned entity has a higher ID
        // Entity IDs are 0 and 1; spawned entity is ID 1
        // Original entity is e1 (ID 0)
        var spawnedId = 1; // next available ID
        var spawnedEntity = new Entity(spawnedId, 1);
        await Assert.That(_world.IsAlive(spawnedEntity)).IsTrue();
        await Assert.That(_world.HasComponent<TestVelocity>(spawnedEntity)).IsTrue();
        var vel = _world.GetComponent<TestVelocity>(spawnedEntity);
        await Assert.That(vel.X).IsEqualTo(42f);
    }

    [Test]
    public async Task ECB_ChunkSystemWithECB_SpawnsEntities()
    {
        var e = _world.Spawn();
        _world.AddComponent(e, new TestVelocity { X = 7, Y = 0, Z = 0 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestChunkSpawnSystem>()
            .Build<SequentialWaveScheduler>();

        int countBefore = _world.EntityCount;
        schedule.Run();

        // TestChunkSpawnSystem spawns one entity per entity in chunk
        await Assert.That(_world.EntityCount).IsEqualTo(countBefore + 1);
    }

    [Test]
    public async Task ECB_SystemWithoutECB_StillWorks()
    {
        // Existing systems without ECB fields should work unchanged
        var e = _world.Spawn();
        _world.AddComponent(e, new TestPosition { X = 10, Y = 20, Z = 0 });
        _world.AddComponent(e, new TestVelocity { X = 1, Y = 2, Z = 0 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestMovementSystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();

        var pos = _world.GetComponent<TestPosition>(e);
        await Assert.That(pos.X).IsEqualTo(11f);
        await Assert.That(pos.Y).IsEqualTo(22f);
    }

    [Test]
    public async Task ECB_MixedSchedule_ECBAndNonECBSystemsTogether()
    {
        var e = _world.Spawn();
        _world.AddComponent(e, new TestPosition { X = 5, Y = 0, Z = 0 });
        _world.AddComponent(e, new TestVelocity { X = 1, Y = 0, Z = 0 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestMovementSystem>()
            .Add<TestSpawnOnUpdateSystem>()
            .Build<SequentialWaveScheduler>();

        int countBefore = _world.EntityCount;
        schedule.Run();

        // MovementSystem updated position, SpawnOnUpdateSystem spawned an entity
        var pos = _world.GetComponent<TestPosition>(e);
        await Assert.That(pos.X).IsNotEqualTo(5f); // position was updated
        await Assert.That(_world.EntityCount).IsEqualTo(countBefore + 1);
    }

    // ---- ECB Playback Tests (end-of-run playback) ----

    [Test]
    public async Task ECB_SpawnedEntitiesNotVisibleDuringExecution()
    {
        // ECB playback happens after all waves complete, so spawned entities
        // are NOT visible to any work items within the same Run() call.
        var e = _world.Spawn();
        _world.AddComponent(e, new TestPosition { X = 1, Y = 0, Z = 0 });
        _world.AddComponent(e, new TestVelocity());

        var schedule = SystemSchedule.Create(_world)
            .Add<TestSpawnOnUpdateSystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();

        // Only 1 entity should be spawned (from the original entity), not more
        await Assert.That(_world.EntityCount).IsEqualTo(2);
    }

    [Test]
    public async Task ECB_SpawnedEntitiesNotVisibleToLaterWaves()
    {
        // TestSpawnOnUpdateSystem (wave 1): spawns entity with Velocity{X=10}
        // TestDoubleVelocityAfterSpawnSystem [After<SpawnOnUpdate>] (wave 2): doubles Velocity
        // ECB playback is end-of-run, so spawned entity is NOT processed by wave 2.
        var e = _world.Spawn();
        _world.AddComponent(e, new TestPosition { X = 10, Y = 0, Z = 0 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestSpawnOnUpdateSystem>()
            .Add<TestDoubleVelocityAfterSpawnSystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();

        var spawnedEntity = new Entity(1, 1);
        await Assert.That(_world.IsAlive(spawnedEntity)).IsTrue();
        var vel = _world.GetComponent<TestVelocity>(spawnedEntity);
        // Velocity is NOT doubled: spawned entity wasn't visible during execution.
        await Assert.That(vel.X).IsEqualTo(10f);
    }

    [Test]
    public async Task ECB_MultipleRunsWorkCorrectly()
    {
        var e = _world.Spawn();
        _world.AddComponent(e, new TestPosition { X = 5, Y = 0, Z = 0 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestSpawnOnUpdateSystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();
        await Assert.That(_world.EntityCount).IsEqualTo(2); // original + 1 spawned

        // Second run: original has Position, spawned has Velocity.
        // SpawnOnUpdateSystem queries Position, so only original triggers a spawn.
        schedule.Run();
        await Assert.That(_world.EntityCount).IsEqualTo(3); // +1 more spawned
    }

    // ---- Entity Handle Injection Tests ----

    [Test]
    public async Task ECB_EntityInjection_DespawnsCorrectEntity()
    {
        // Spawn entities with varying health
        var alive1 = _world.Spawn();
        _world.AddComponent(alive1, new TestHealth { Current = 100, Max = 100 });

        var dead1 = _world.Spawn();
        _world.AddComponent(dead1, new TestHealth { Current = 0, Max = 100 });

        var alive2 = _world.Spawn();
        _world.AddComponent(alive2, new TestHealth { Current = 50, Max = 100 });

        var dead2 = _world.Spawn();
        _world.AddComponent(dead2, new TestHealth { Current = -10, Max = 100 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestDespawnDeadSystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();

        // Only entities with health <= 0 should be despawned
        await Assert.That(_world.IsAlive(alive1)).IsTrue();
        await Assert.That(_world.IsAlive(alive2)).IsTrue();
        await Assert.That(_world.IsAlive(dead1)).IsFalse();
        await Assert.That(_world.IsAlive(dead2)).IsFalse();
        await Assert.That(_world.EntityCount).IsEqualTo(2);
    }

    [Test]
    public async Task ECB_EntityInjection_EntityHandleIsCorrect()
    {
        // Spawn entity with Health, run TestRecordEntitySystem which adds TestDamage with Entity.Id
        var e = _world.Spawn();
        _world.AddComponent(e, new TestHealth { Current = 42, Max = 100 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestRecordEntitySystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();

        // The system should have recorded the entity's ID in TestDamage.Amount
        await Assert.That(_world.HasComponent<TestDamage>(e)).IsTrue();
        var dmg = _world.GetComponent<TestDamage>(e);
        await Assert.That(dmg.Amount).IsEqualTo(e.Id);
    }

    [Test]
    public async Task ECB_ChunkEntitySpan_DespawnsCorrectEntities()
    {
        // Spawn entities with varying health
        var alive = _world.Spawn();
        _world.AddComponent(alive, new TestHealth { Current = 100, Max = 100 });

        var dead = _world.Spawn();
        _world.AddComponent(dead, new TestHealth { Current = 0, Max = 100 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestChunkEntitySpanSystem>()
            .Build<SequentialWaveScheduler>();

        schedule.Run();

        // Only entity with health <= 0 should be despawned
        await Assert.That(_world.IsAlive(alive)).IsTrue();
        await Assert.That(_world.IsAlive(dead)).IsFalse();
        await Assert.That(_world.EntityCount).IsEqualTo(1);
    }

    [Test]
    public async Task ECB_NoCommandsRecorded_HealthyEntitiesNotDespawned()
    {
        // Entity has TestHealth with health > 0 — TestDespawnDeadSystem should not despawn
        var e = _world.Spawn();
        _world.AddComponent(e, new TestHealth { Current = 100, Max = 100 });

        var schedule = SystemSchedule.Create(_world)
            .Add<TestDespawnDeadSystem>()
            .Build<SequentialWaveScheduler>();

        int countBefore = _world.EntityCount;
        schedule.Run();

        // No despawns since health > 0
        await Assert.That(_world.EntityCount).IsEqualTo(countBefore);
    }
}
