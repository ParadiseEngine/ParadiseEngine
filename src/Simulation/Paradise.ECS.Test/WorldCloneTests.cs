using TUnit.Assertions.Enums;

namespace Paradise.ECS.Test;

/// <summary>Tests for World.CopyFrom() functionality.</summary>
public sealed class WorldCloneTests : IDisposable
{
    private readonly SharedWorld<SmallBitSet<ulong>, DefaultConfig> _sharedWorld;
    private readonly World<SmallBitSet<ulong>, DefaultConfig> _sourceWorld;
    private readonly World<SmallBitSet<ulong>, DefaultConfig> _targetWorld;

    public WorldCloneTests()
    {
        _sharedWorld = new SharedWorld<SmallBitSet<ulong>, DefaultConfig>(ComponentRegistry.Shared.TypeInfos);
        _sourceWorld = _sharedWorld.CreateWorld();
        _targetWorld = _sharedWorld.CreateWorld();
    }

    public void Dispose()
    {
        _sharedWorld.Dispose();
    }

    #region Basic Clone Tests

    [Test]
    public async Task CopyFrom_CopiesAllEntities()
    {
        _sourceWorld.Spawn();
        _sourceWorld.Spawn();
        _sourceWorld.Spawn();

        _targetWorld.CopyFrom(_sourceWorld);

        await Assert.That(_targetWorld.EntityCount).IsEqualTo(3);
    }

    [Test]
    public async Task CopyFrom_CopiesComponentData()
    {
        var entity = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(entity, new TestPosition { X = 10.5f, Y = 20.5f, Z = 30.5f });
        _sourceWorld.AddComponent(entity, new TestVelocity { X = 1.0f, Y = 2.0f, Z = 3.0f });

        _targetWorld.CopyFrom(_sourceWorld);

        // Assert - Entity handles from source world are valid in target after clone
        var pos = _targetWorld.GetComponent<TestPosition>(entity);
        var vel = _targetWorld.GetComponent<TestVelocity>(entity);

        await Assert.That(pos.X).IsEqualTo(10.5f);
        await Assert.That(pos.Y).IsEqualTo(20.5f);
        await Assert.That(pos.Z).IsEqualTo(30.5f);
        await Assert.That(vel.X).IsEqualTo(1.0f);
        await Assert.That(vel.Y).IsEqualTo(2.0f);
        await Assert.That(vel.Z).IsEqualTo(3.0f);
    }

    [Test]
    public async Task CopyFrom_EntityHandlesRemainValid()
    {
        var entity1 = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(entity1, new TestPosition { X = 100, Y = 200 });
        var entity2 = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(entity2, new TestHealth { Current = 50, Max = 100 });

        _targetWorld.CopyFrom(_sourceWorld);

        // Assert - Original entity handles work in target world
        await Assert.That(_targetWorld.IsAlive(entity1)).IsTrue();
        await Assert.That(_targetWorld.IsAlive(entity2)).IsTrue();
        await Assert.That(_targetWorld.HasComponent<TestPosition>(entity1)).IsTrue();
        await Assert.That(_targetWorld.HasComponent<TestHealth>(entity2)).IsTrue();
    }

    [Test]
    public async Task CopyFrom_ClearsTargetFirst()
    {
        // Arrange - Add entities to both worlds with different components
        var sourceEntity = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(sourceEntity, new TestPosition { X = 1, Y = 2 });

        // Create target entities - the first one will have same ID as sourceEntity
        var targetEntity1 = _targetWorld.Spawn();
        _targetWorld.AddComponent(targetEntity1, new TestVelocity { X = 5, Y = 6 }); // Different component type
        var targetEntity2 = _targetWorld.Spawn();
        _targetWorld.AddComponent(targetEntity2, new TestHealth { Current = 100, Max = 100 });
        var targetEntity3 = _targetWorld.Spawn();

        await Assert.That(_targetWorld.EntityCount).IsEqualTo(3);

        _targetWorld.CopyFrom(_sourceWorld);

        // Assert - Target should only have source entities
        await Assert.That(_targetWorld.EntityCount).IsEqualTo(1);
        await Assert.That(_targetWorld.IsAlive(sourceEntity)).IsTrue();

        // Verify the data is from source (TestPosition), not original target (TestVelocity)
        // After clone, entity at ID 0 has Position from source, not Velocity from target
        await Assert.That(_targetWorld.HasComponent<TestPosition>(sourceEntity)).IsTrue();
        await Assert.That(_targetWorld.HasComponent<TestVelocity>(sourceEntity)).IsFalse();
        var pos = _targetWorld.GetComponent<TestPosition>(sourceEntity);
        await Assert.That(pos.X).IsEqualTo(1);
        await Assert.That(pos.Y).IsEqualTo(2);

        // targetEntity2 and targetEntity3 have IDs > 0, so they shouldn't exist after clone
        await Assert.That(_targetWorld.IsAlive(targetEntity2)).IsFalse();
        await Assert.That(_targetWorld.IsAlive(targetEntity3)).IsFalse();
    }

    [Test]
    public async Task CopyFrom_EmptyWorld()
    {
        // Arrange - Source is empty, target has entities
        _targetWorld.Spawn();
        _targetWorld.Spawn();
        await Assert.That(_targetWorld.EntityCount).IsEqualTo(2);

        _targetWorld.CopyFrom(_sourceWorld);

        await Assert.That(_targetWorld.EntityCount).IsEqualTo(0);
    }

    [Test]
    public async Task CopyFrom_MultipleArchetypes()
    {
        // Arrange - Create entities with different component combinations
        var entity1 = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(entity1, new TestPosition { X = 1, Y = 1 });

        var entity2 = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(entity2, new TestVelocity { X = 2, Y = 2 });

        var entity3 = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(entity3, new TestPosition { X = 3, Y = 3 });
        _sourceWorld.AddComponent(entity3, new TestVelocity { X = 4, Y = 4 });

        var entity4 = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(entity4, new TestHealth { Current = 100, Max = 100 });

        var entity5 = _sourceWorld.Spawn(); // Empty archetype

        _targetWorld.CopyFrom(_sourceWorld);

        await Assert.That(_targetWorld.EntityCount).IsEqualTo(5);

        await Assert.That(_targetWorld.HasComponent<TestPosition>(entity1)).IsTrue();
        await Assert.That(_targetWorld.HasComponent<TestVelocity>(entity1)).IsFalse();

        await Assert.That(_targetWorld.HasComponent<TestVelocity>(entity2)).IsTrue();
        await Assert.That(_targetWorld.HasComponent<TestPosition>(entity2)).IsFalse();

        await Assert.That(_targetWorld.HasComponent<TestPosition>(entity3)).IsTrue();
        await Assert.That(_targetWorld.HasComponent<TestVelocity>(entity3)).IsTrue();

        await Assert.That(_targetWorld.HasComponent<TestHealth>(entity4)).IsTrue();

        await Assert.That(_targetWorld.HasComponent<TestPosition>(entity5)).IsFalse();
        await Assert.That(_targetWorld.HasComponent<TestVelocity>(entity5)).IsFalse();
        await Assert.That(_targetWorld.HasComponent<TestHealth>(entity5)).IsFalse();

        var pos1 = _targetWorld.GetComponent<TestPosition>(entity1);
        await Assert.That(pos1.X).IsEqualTo(1);

        var pos3 = _targetWorld.GetComponent<TestPosition>(entity3);
        var vel3 = _targetWorld.GetComponent<TestVelocity>(entity3);
        await Assert.That(pos3.X).IsEqualTo(3);
        await Assert.That(vel3.X).IsEqualTo(4);

        var health4 = _targetWorld.GetComponent<TestHealth>(entity4);
        await Assert.That(health4.Current).IsEqualTo(100);
    }

    #endregion

    #region Entity Lifecycle Tests

    [Test]
    public async Task CopyFrom_PreservesFreeSlots()
    {
        // Arrange - Create and despawn some entities to create free slots
        var e1 = _sourceWorld.Spawn();
        var e2 = _sourceWorld.Spawn();
        var e3 = _sourceWorld.Spawn();

        _sourceWorld.Despawn(e2); // Create a free slot in the middle

        _targetWorld.CopyFrom(_sourceWorld);

        await Assert.That(_targetWorld.EntityCount).IsEqualTo(2);
        await Assert.That(_targetWorld.IsAlive(e1)).IsTrue();
        await Assert.That(_targetWorld.IsAlive(e2)).IsFalse(); // e2 was despawned
        await Assert.That(_targetWorld.IsAlive(e3)).IsTrue();
    }

    [Test]
    public async Task CopyFrom_PreservesEntityVersions()
    {
        // Arrange - Create entity, despawn it, create new one to bump version
        var e1 = _sourceWorld.Spawn();
        _sourceWorld.Despawn(e1);

        var e2 = _sourceWorld.Spawn(); // Should reuse e1's slot with bumped version
        _sourceWorld.AddComponent(e2, new TestPosition { X = 42, Y = 42 });

        _targetWorld.CopyFrom(_sourceWorld);

        await Assert.That(_targetWorld.IsAlive(e1)).IsFalse(); // Old version should be invalid
        await Assert.That(_targetWorld.IsAlive(e2)).IsTrue(); // New version should be valid
        var pos = _targetWorld.GetComponent<TestPosition>(e2);
        await Assert.That(pos.X).IsEqualTo(42);
    }

    #endregion

    #region Error Handling Tests

    [Test]
    public async Task CopyFrom_ThrowsOnNullSource()
    {
        await Assert.That(() => _targetWorld.CopyFrom(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task CopyFrom_ThrowsOnDifferentSharedWorld()
    {
        // Arrange - Create a world from a different SharedWorld
        using var otherSharedWorld = new SharedWorld<SmallBitSet<ulong>, DefaultConfig>(ComponentRegistry.Shared.TypeInfos);
        var otherWorld = otherSharedWorld.CreateWorld();

        // Act & Assert - Copying from a world with different SharedMetadata should throw
        await Assert.That(() => _targetWorld.CopyFrom(otherWorld)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task CopyFrom_ThrowsOnSelfCopy()
    {
        // Arrange - Create some entities so self-copy would be destructive
        _sourceWorld.Spawn();
        _sourceWorld.Spawn();
        _sourceWorld.Spawn();

        // Act & Assert - Copying a world to itself should throw
        await Assert.That(() => _sourceWorld.CopyFrom(_sourceWorld)).Throws<InvalidOperationException>();
    }

    #endregion

    #region Multi-Chunk Tests

    [Test]
    public async Task CopyFrom_CopiesMultipleChunks()
    {
        // Populate 1000 Position entities; the separate large-count test exceeds chunk capacity.
        const int entityCount = 1000;
        var entities = new Entity[entityCount];

        for (int i = 0; i < entityCount; i++)
        {
            entities[i] = _sourceWorld.Spawn();
            _sourceWorld.AddComponent(entities[i], new TestPosition { X = i, Y = i * 2, Z = i * 3 });
        }

        _targetWorld.CopyFrom(_sourceWorld);

        await Assert.That(_targetWorld.EntityCount).IsEqualTo(entityCount);

        // Verify some entities across the range
        var pos0 = _targetWorld.GetComponent<TestPosition>(entities[0]);
        await Assert.That(pos0.X).IsEqualTo(0);

        var pos500 = _targetWorld.GetComponent<TestPosition>(entities[500]);
        await Assert.That(pos500.X).IsEqualTo(500);

        var pos999 = _targetWorld.GetComponent<TestPosition>(entities[999]);
        await Assert.That(pos999.X).IsEqualTo(999);
    }

    #endregion

    #region Query Tests

    [Test]
    public async Task CopyFrom_QueriesWorkOnClonedWorld()
    {
        for (int i = 0; i < 5; i++)
        {
            var entity = _sourceWorld.Spawn();
            _sourceWorld.AddComponent(entity, new TestPosition { X = i, Y = i });
        }

        for (int i = 0; i < 3; i++)
        {
            var entity = _sourceWorld.Spawn();
            _sourceWorld.AddComponent(entity, new TestVelocity { X = i, Y = i });
        }

        _targetWorld.CopyFrom(_sourceWorld);

        // Assert - Query should work on cloned world
        var posQuery = QueryBuilder<SmallBitSet<ulong>>.Create().With<TestPosition>().Build(_targetWorld.ArchetypeRegistry);
        var velQuery = QueryBuilder<SmallBitSet<ulong>>.Create().With<TestVelocity>().Build(_targetWorld.ArchetypeRegistry);

        int posCount = 0;
        foreach (var _ in posQuery)
            posCount++;

        int velCount = 0;
        foreach (var _ in velQuery)
            velCount++;

        await Assert.That(posCount).IsEqualTo(5);
        await Assert.That(velCount).IsEqualTo(3);
    }

    #endregion

    #region Isolation Tests

    [Test]
    public async Task CopyFrom_SourceAndTargetAreIndependent()
    {
        var entity = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(entity, new TestPosition { X = 10, Y = 20 });

        _targetWorld.CopyFrom(_sourceWorld);

        // Modify source after clone
        _sourceWorld.GetComponent<TestPosition>(entity) = new TestPosition { X = 100, Y = 200 };

        // Assert - Target should have original values
        var sourcePos = _sourceWorld.GetComponent<TestPosition>(entity);
        var targetPos = _targetWorld.GetComponent<TestPosition>(entity);

        await Assert.That(sourcePos.X).IsEqualTo(100);
        await Assert.That(sourcePos.Y).IsEqualTo(200);
        await Assert.That(targetPos.X).IsEqualTo(10);
        await Assert.That(targetPos.Y).IsEqualTo(20);
    }

    [Test]
    public async Task CopyFrom_ModifyingTargetDoesNotAffectSource()
    {
        var entity = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(entity, new TestPosition { X = 10, Y = 20 });

        _targetWorld.CopyFrom(_sourceWorld);

        // Modify target after clone
        _targetWorld.GetComponent<TestPosition>(entity) = new TestPosition { X = 100, Y = 200 };

        // Assert - Source should have original values
        var sourcePos = _sourceWorld.GetComponent<TestPosition>(entity);
        var targetPos = _targetWorld.GetComponent<TestPosition>(entity);

        await Assert.That(sourcePos.X).IsEqualTo(10);
        await Assert.That(sourcePos.Y).IsEqualTo(20);
        await Assert.That(targetPos.X).IsEqualTo(100);
        await Assert.That(targetPos.Y).IsEqualTo(200);
    }

    [Test]
    public async Task CopyFrom_NewEntitiesInTargetAreIndependent()
    {
        var sourceEntity = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(sourceEntity, new TestPosition { X = 1, Y = 2 });

        _targetWorld.CopyFrom(_sourceWorld);

        // Create new entity in target after clone
        var newEntity = _targetWorld.Spawn();
        _targetWorld.AddComponent(newEntity, new TestVelocity { X = 5, Y = 6 });

        await Assert.That(_sourceWorld.EntityCount).IsEqualTo(1);
        await Assert.That(_targetWorld.EntityCount).IsEqualTo(2);
        await Assert.That(_sourceWorld.IsAlive(newEntity)).IsFalse();
        await Assert.That(_targetWorld.IsAlive(newEntity)).IsTrue();
    }

    #endregion

    #region Multiple Clone Tests

    [Test]
    public async Task CopyFrom_CanCloneMultipleTimes()
    {
        var entity = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(entity, new TestPosition { X = 1, Y = 1 });

        _targetWorld.CopyFrom(_sourceWorld);
        await Assert.That(_targetWorld.EntityCount).IsEqualTo(1);

        _sourceWorld.GetComponent<TestPosition>(entity) = new TestPosition { X = 2, Y = 2 };
        var entity2 = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(entity2, new TestVelocity { X = 3, Y = 3 });

        _targetWorld.CopyFrom(_sourceWorld);

        // Assert - Target should have updated state
        await Assert.That(_targetWorld.EntityCount).IsEqualTo(2);
        var pos = _targetWorld.GetComponent<TestPosition>(entity);
        await Assert.That(pos.X).IsEqualTo(2);
        await Assert.That(pos.Y).IsEqualTo(2);
    }

    [Test]
    public async Task CopyFrom_CanCopyToMultipleTargets()
    {
        var entity = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(entity, new TestPosition { X = 10, Y = 20 });

        var target2 = _sharedWorld.CreateWorld();

        _targetWorld.CopyFrom(_sourceWorld);
        target2.CopyFrom(_sourceWorld);

        // Assert - Both targets should have the same data
        await Assert.That(_targetWorld.EntityCount).IsEqualTo(1);
        await Assert.That(target2.EntityCount).IsEqualTo(1);

        var pos1 = _targetWorld.GetComponent<TestPosition>(entity);
        var pos2 = target2.GetComponent<TestPosition>(entity);

        await Assert.That(pos1.X).IsEqualTo(10);
        await Assert.That(pos2.X).IsEqualTo(10);
    }

    #endregion

    #region Edge Case Tests

    [Test]
    public async Task CopyFrom_WithSparseArchetypeIds()
    {
        // Arrange - Create entities in an intermediate world to create archetype IDs
        // that won't be used in the source world, causing sparse archetype IDs
        var intermediateWorld = _sharedWorld.CreateWorld();

        // Create archetypes in intermediate world (these get global IDs 1, 2, 3...)
        var tempEntity1 = intermediateWorld.Spawn();
        intermediateWorld.AddComponent(tempEntity1, new TestHealth { Current = 1, Max = 1 });

        var tempEntity2 = intermediateWorld.Spawn();
        intermediateWorld.AddComponent(tempEntity2, new TestVelocity { X = 1, Y = 1 });

        // Now create entities in source world with different archetypes
        // This creates a gap in the source world's archetype list
        var sourceEntity = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(sourceEntity, new TestPosition { X = 42, Y = 42 });

        _targetWorld.CopyFrom(_sourceWorld);

        await Assert.That(_targetWorld.EntityCount).IsEqualTo(1);
        await Assert.That(_targetWorld.IsAlive(sourceEntity)).IsTrue();
        var pos = _targetWorld.GetComponent<TestPosition>(sourceEntity);
        await Assert.That(pos.X).IsEqualTo(42);
    }

    [Test]
    public async Task CopyFrom_EmptySourceWithExistingArchetypes()
    {
        // Arrange - Create archetype in source but no entities
        // This ensures the archetype exists but has no data to copy
        var tempEntity = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(tempEntity, new TestPosition { X = 1, Y = 1 });
        _sourceWorld.Despawn(tempEntity);

        // Target has some entities
        var targetEntity = _targetWorld.Spawn();
        _targetWorld.AddComponent(targetEntity, new TestVelocity { X = 5, Y = 5 });

        _targetWorld.CopyFrom(_sourceWorld);

        // Assert - Target should be cleared
        await Assert.That(_targetWorld.EntityCount).IsEqualTo(0);
    }

    [Test]
    public async Task CopyFrom_LargeEntityCount()
    {
        // Arrange - Create many entities to test chunk copying across multiple chunks
        const int entityCount = 5000;
        var entities = new Entity[entityCount];

        for (int i = 0; i < entityCount; i++)
        {
            entities[i] = _sourceWorld.Spawn();
            _sourceWorld.AddComponent(entities[i], new TestPosition { X = i, Y = i * 2, Z = i * 3 });
            _sourceWorld.AddComponent(entities[i], new TestVelocity { X = i * 0.1f, Y = i * 0.2f, Z = i * 0.3f });
        }

        _targetWorld.CopyFrom(_sourceWorld);

        await Assert.That(_targetWorld.EntityCount).IsEqualTo(entityCount);

        // Verify first, middle, and last entities
        var pos0 = _targetWorld.GetComponent<TestPosition>(entities[0]);
        await Assert.That(pos0.X).IsEqualTo(0);

        var pos2500 = _targetWorld.GetComponent<TestPosition>(entities[2500]);
        await Assert.That(pos2500.X).IsEqualTo(2500);

        var pos4999 = _targetWorld.GetComponent<TestPosition>(entities[4999]);
        await Assert.That(pos4999.X).IsEqualTo(4999);

        var vel4999 = _targetWorld.GetComponent<TestVelocity>(entities[4999]);
        await Assert.That(vel4999.X).IsEqualTo(4999 * 0.1f);
    }

    [Test]
    public async Task CopyFrom_WithTagComponents()
    {
        // Arrange - Create entities with tag components (zero-size)
        var entity = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(entity, new TestPosition { X = 10, Y = 20 });
        _sourceWorld.AddComponent<TestTag>(entity);

        _targetWorld.CopyFrom(_sourceWorld);

        await Assert.That(_targetWorld.EntityCount).IsEqualTo(1);
        await Assert.That(_targetWorld.HasComponent<TestPosition>(entity)).IsTrue();
        await Assert.That(_targetWorld.HasComponent<TestTag>(entity)).IsTrue();
        var pos = _targetWorld.GetComponent<TestPosition>(entity);
        await Assert.That(pos.X).IsEqualTo(10);
    }

    [Test]
    public async Task CopyFrom_PreservesEntityIdAllocation()
    {
        // Arrange - Create and despawn entities to advance the entity ID counter
        for (int i = 0; i < 10; i++)
        {
            var temp = _sourceWorld.Spawn();
            if (i % 2 == 0)
                _sourceWorld.Despawn(temp);
        }

        _targetWorld.CopyFrom(_sourceWorld);

        // Assert - New entities in target should get correct IDs
        // (continuing from where source left off)
        var newEntity = _targetWorld.Spawn();
        // The copied allocator must still produce a live entity.
        await Assert.That(_targetWorld.IsAlive(newEntity)).IsTrue();
    }

    #endregion

    #region Reused Target Tests

    [Test]
    public async Task CopyFrom_WarmCopyAllocatesNothing()
    {
        // A snapshot host's tick: copy into a pooled world and read a query resolved on it earlier,
        // with freed entity IDs waiting in the source's allocator for reuse.
        const int entityCount = 3000;
        var entities = new Entity[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            entities[i] = _sourceWorld.Spawn();
            _sourceWorld.AddComponent(entities[i], new TestPosition { X = i });
            if (i % 3 == 0)
                _sourceWorld.AddComponent(entities[i], new TestVelocity { X = i });
            if (i % 5 == 0)
                _sourceWorld.AddComponent(entities[i], new TestHealth { Current = i, Max = i });
        }
        int despawned = 0;
        for (int i = 0; i < entityCount; i += 7, despawned++)
            _sourceWorld.Despawn(entities[i]);
        var positions = QueryOf<TestPosition>(_targetWorld);
        _targetWorld.CopyFrom(_sourceWorld);
        _targetWorld.CopyFrom(_sourceWorld);
        _sourceWorld.GetComponent<TestPosition>(entities[^1]).X = -1;

        long before = GC.GetAllocatedBytesForCurrentThread();
        _targetWorld.CopyFrom(_sourceWorld);
        int count = positions.EntityCount;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0L);
        await Assert.That(count).IsEqualTo(entityCount - despawned);
        await Assert.That(_targetWorld.GetComponent<TestPosition>(entities[^1]).X).IsEqualTo(-1f);
        // The copy hands freed IDs out in the order the source would.
        for (int i = 0; i < despawned + 2; i++)
            await Assert.That(_targetWorld.Spawn()).IsEqualTo(_sourceWorld.Spawn());
    }

    [Test]
    public async Task CopyFrom_QueryResolvedBeforeCopyReadsCopiedData()
    {
        var kept = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(kept, new TestPosition { X = 1 });
        var removed = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(removed, new TestPosition { X = 2 });
        _targetWorld.CopyFrom(_sourceWorld);
        var positions = QueryOf<TestPosition>(_targetWorld);

        _sourceWorld.Despawn(removed);
        var moved = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(moved, new TestVelocity { X = 3 });
        _sourceWorld.AddComponent(moved, new TestPosition { X = 3 });
        _targetWorld.CopyFrom(_sourceWorld);

        await Assert.That(Ids(positions)).IsEquivalentTo(new List<int> { kept.Id, moved.Id }, CollectionOrdering.Any);
    }

    [Test]
    public async Task CopyFrom_ReusedTargetMatchesFreshCopyAcrossStructuralChanges()
    {
        // Past one chunk of Position-only entities
        var entities = new List<Entity>();
        for (int i = 0; i < 1500; i++)
        {
            var entity = _sourceWorld.Spawn();
            _sourceWorld.AddComponent(entity, new TestPosition { X = i });
            entities.Add(entity);
        }
        var grown = CopyIntoTargetAndFresh(entities);
        await Assert.That(grown.Target).IsEqualTo(grown.Fresh);

        // Half move to {Position, Velocity}, so the Position-only archetype shrinks to one chunk
        for (int i = 0; i < entities.Count; i += 2)
            _sourceWorld.AddComponent(entities[i], new TestVelocity { X = i });
        var moved = CopyIntoTargetAndFresh(entities);
        await Assert.That(moved.Target).IsEqualTo(moved.Fresh);

        // {Position, Velocity} empties
        for (int i = 0; i < entities.Count; i += 2)
            _sourceWorld.Despawn(entities[i]);
        var emptied = CopyIntoTargetAndFresh(entities);
        await Assert.That(emptied.Target).IsEqualTo(emptied.Fresh);

        // Some move to the empty archetype, and a new archetype appears
        for (int i = 1; i < 200; i += 2)
            _sourceWorld.RemoveComponent<TestPosition>(entities[i]);
        var healthy = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(healthy, new TestHealth { Current = 1, Max = 1 });
        entities.Add(healthy);
        var created = CopyIntoTargetAndFresh(entities);
        await Assert.That(created.Target).IsEqualTo(created.Fresh);
    }

    [Test]
    public async Task CopyFrom_DropsArchetypesTheSourceLacks()
    {
        var other = _sharedWorld.CreateWorld();
        var healthy = other.Spawn();
        other.AddComponent(healthy, new TestHealth { Current = 1, Max = 1 });
        _targetWorld.CopyFrom(other);
        await Assert.That(QueryOf<TestHealth>(_targetWorld).ArchetypeCount).IsEqualTo(1);

        var entity = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(entity, new TestPosition { X = 1 });
        _targetWorld.CopyFrom(_sourceWorld);

        await Assert.That(QueryOf<TestHealth>(_targetWorld).ArchetypeCount).IsEqualTo(0);
        await Assert.That(QueryOf<TestPosition>(_targetWorld).EntityCount).IsEqualTo(1);
    }

    [Test]
    public async Task CopyFrom_QueryResolvedBeforeMismatchedCopyReadsCopiedData()
    {
        // The target holds {Health}, which the source lacks, so the copy releases the target's archetypes
        var other = _sharedWorld.CreateWorld();
        other.AddComponent(other.Spawn(), new TestPosition { X = 1 });
        other.AddComponent(other.Spawn(), new TestHealth { Current = 1, Max = 1 });
        _targetWorld.CopyFrom(other);
        var positions = QueryOf<TestPosition>(_targetWorld);
        var healths = QueryOf<TestHealth>(_targetWorld);

        var copied = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            var entity = _sourceWorld.Spawn();
            _sourceWorld.AddComponent(entity, new TestPosition { X = i });
            copied.Add(entity.Id);
        }
        _targetWorld.CopyFrom(_sourceWorld);

        await Assert.That(healths.ArchetypeCount).IsEqualTo(0);
        await Assert.That(Ids(positions)).IsEquivalentTo(copied, CollectionOrdering.Any);
    }

    [Test]
    public async Task CopyFrom_QueryOrderMatchesFreshCopy()
    {
        // Another world takes {Velocity, Position}'s archetype ID first, so the source creates it after
        // {Position} although its ID is lower; a reused target must still list them as a fresh copy does.
        var other = _sharedWorld.CreateWorld();
        var early = other.Spawn();
        other.AddComponent(early, new TestVelocity { X = 1 });
        other.AddComponent(early, new TestPosition { X = 1 });

        var still = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(still, new TestPosition { X = 2 });
        _targetWorld.CopyFrom(_sourceWorld);
        _ = QueryOf<TestPosition>(_targetWorld); // cached on the target before {Velocity, Position} reaches it

        var moved = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(moved, new TestVelocity { X = 3 });
        _sourceWorld.AddComponent(moved, new TestPosition { X = 3 });
        _targetWorld.CopyFrom(_sourceWorld);
        var fresh = _sharedWorld.CreateWorld();
        fresh.CopyFrom(_sourceWorld);

        await Assert.That(Ids(QueryOf<TestPosition>(_targetWorld))).IsEquivalentTo(Ids(QueryOf<TestPosition>(fresh)), CollectionOrdering.Matching);
    }

    [Test]
    public async Task CopyFrom_QueryOrderMatchesFreshCopyAfterTargetCreatesArchetypes()
    {
        // Another world takes {Velocity, Position}'s archetype ID before {Position}'s
        var other = _sharedWorld.CreateWorld();
        var early = other.Spawn();
        other.AddComponent(early, new TestVelocity { X = 1 });
        other.AddComponent(early, new TestPosition { X = 1 });

        var still = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(still, new TestPosition { X = 2 });
        var moved = _sourceWorld.Spawn();
        _sourceWorld.AddComponent(moved, new TestPosition { X = 3 });
        _sourceWorld.AddComponent(moved, new TestVelocity { X = 3 });

        // The target itself creates {Position}, then {Velocity, Position}, appending both to a cached query
        // against ID order; the copy then has no archetype to create.
        var positions = QueryOf<TestPosition>(_targetWorld);
        var local = _targetWorld.Spawn();
        _targetWorld.AddComponent(local, new TestPosition { X = 4 });
        _targetWorld.AddComponent(local, new TestVelocity { X = 4 });
        _targetWorld.CopyFrom(_sourceWorld);
        var fresh = _sharedWorld.CreateWorld();
        fresh.CopyFrom(_sourceWorld);

        await Assert.That(Ids(positions)).IsEquivalentTo(Ids(QueryOf<TestPosition>(fresh)), CollectionOrdering.Matching);
    }

    /// <summary>Copies the source into the reused target and into a fresh world, describing both.</summary>
    private (string Target, string Fresh) CopyIntoTargetAndFresh(List<Entity> entities)
    {
        _targetWorld.CopyFrom(_sourceWorld);
        var fresh = _sharedWorld.CreateWorld();
        fresh.CopyFrom(_sourceWorld);
        return (Describe(_targetWorld, entities), Describe(fresh, entities));
    }

    /// <summary>Each query's entity order and chunk count, then every tracked entity's components.</summary>
    private static string Describe(World<SmallBitSet<ulong>, DefaultConfig> world, List<Entity> entities)
    {
        var text = new System.Text.StringBuilder();
        AppendQuery(text, QueryOf<TestPosition>(world));
        AppendQuery(text, QueryOf<TestVelocity>(world));
        AppendQuery(text, QueryOf<TestHealth>(world));
        foreach (var entity in entities)
        {
            if (!world.IsAlive(entity))
            {
                text.AppendLine("-");
                continue;
            }
            text.Append(world.HasComponent<TestPosition>(entity) ? world.GetComponent<TestPosition>(entity).X : float.NaN)
                .Append(' ')
                .Append(world.HasComponent<TestVelocity>(entity) ? world.GetComponent<TestVelocity>(entity).X : float.NaN)
                .Append(' ')
                .Append(world.HasComponent<TestHealth>(entity) ? world.GetComponent<TestHealth>(entity).Current : -1)
                .AppendLine();
        }
        return text.ToString();
    }

    private static void AppendQuery(System.Text.StringBuilder text, Query<SmallBitSet<ulong>, DefaultConfig, Archetype<SmallBitSet<ulong>, DefaultConfig>> query)
    {
        int chunks = 0;
        foreach (var _ in query.Chunks)
            chunks++;
        text.AppendJoin(',', Ids(query)).Append(" in ").Append(chunks).AppendLine(" chunks");
    }

    private static Query<SmallBitSet<ulong>, DefaultConfig, Archetype<SmallBitSet<ulong>, DefaultConfig>> QueryOf<T>(World<SmallBitSet<ulong>, DefaultConfig> world)
        where T : unmanaged, IComponent
        => QueryBuilder<SmallBitSet<ulong>>.Create().With<T>().Build(world.ArchetypeRegistry);

    private static List<int> Ids(Query<SmallBitSet<ulong>, DefaultConfig, Archetype<SmallBitSet<ulong>, DefaultConfig>> query)
    {
        var ids = new List<int>();
        foreach (int id in query)
            ids.Add(id);
        return ids;
    }

    #endregion
}
