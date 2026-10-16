namespace Paradise.ECS.Test;

/// <summary>
/// Tests for <c>World.CreateEntity(in TMask)</c> — the overload for a caller whose component set
/// is assembled at runtime rather than spelled as a chain of type arguments.
///
/// The three properties worth pinning are the three a scene loader depends on: the entity lands in
/// the archetype the mask names, every component reads as its zero default, and two entities built
/// from equal masks SHARE an archetype rather than each interning one of their own.
/// </summary>
public sealed class CreateEntityFromMaskTests : IDisposable
{
    private static readonly DefaultConfig s_config = new();
    private readonly ChunkManager _chunkManager = ChunkManager.Create(s_config);
    private readonly SharedArchetypeMetadata<SmallBitSet<ulong>, DefaultConfig> _sharedMetadata = new(ComponentRegistry.Shared.TypeInfos, s_config);
    private readonly World<SmallBitSet<ulong>, DefaultConfig> _world;

    public CreateEntityFromMaskTests()
    {
        _world = new World<SmallBitSet<ulong>, DefaultConfig>(s_config, _sharedMetadata, _chunkManager);
    }

    public void Dispose()
    {
        _sharedMetadata.Dispose();
        _chunkManager.Dispose();
    }

    private static SmallBitSet<ulong> MaskOf(params ComponentId[] ids)
    {
        var mask = SmallBitSet<ulong>.Empty;
        foreach (var id in ids)
        {
            mask = mask.Set(id);
        }
        return mask;
    }

    [Test]
    public async Task Mask_CreatesEntityCarryingExactlyThoseComponents()
    {
        var entity = _world.CreateEntity(MaskOf(TestPosition.TypeId, TestHealth.TypeId));

        await Assert.That(entity.IsValid).IsTrue();
        await Assert.That(_world.HasComponent<TestPosition>(entity)).IsTrue();
        await Assert.That(_world.HasComponent<TestHealth>(entity)).IsTrue();
        // Not a superset: a mask names the whole archetype, not a floor for it.
        await Assert.That(_world.HasComponent<TestVelocity>(entity)).IsFalse();
    }

    [Test]
    public async Task Mask_ComponentsReadAsZeroDefault()
    {
        // What the overload promises in place of a builder's values. A loader seeds what the
        // document authored and leaves the rest alone, so "the rest" has to be default rather than
        // whatever the previous occupant of that chunk slot left behind.
        var entity = _world.CreateEntity(MaskOf(TestPosition.TypeId));

        var position = _world.GetComponent<TestPosition>(entity);
        await Assert.That(position.X).IsEqualTo(0f);
        await Assert.That(position.Y).IsEqualTo(0f);
        await Assert.That(position.Z).IsEqualTo(0f);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Mask_ReusedRowClearsEveryComponentWithoutChangingTheSurvivor(bool removeFirst)
    {
        var first = _world.CreateEntity(EntityBuilder.Create()
            .Add(new TestPosition { X = 11, Y = 12, Z = 13 })
            .Add(new TestHealth { Current = 14, Max = 15 }));
        var last = _world.CreateEntity(EntityBuilder.Create()
            .Add(new TestPosition { X = 21, Y = 22, Z = 23 })
            .Add(new TestHealth { Current = 24, Max = 25 }));
        var survivor = removeFirst ? last : first;
        int expectedPosition = removeFirst ? 21 : 11;
        int expectedHealth = removeFirst ? 24 : 14;

        _world.Despawn(removeFirst ? first : last);
        var replacement = _world.CreateEntity(MaskOf(TestPosition.TypeId, TestHealth.TypeId));
        var position = _world.GetComponent<TestPosition>(replacement);
        var health = _world.GetComponent<TestHealth>(replacement);

        await Assert.That(position.X).IsEqualTo(0f);
        await Assert.That(position.Y).IsEqualTo(0f);
        await Assert.That(position.Z).IsEqualTo(0f);
        await Assert.That(health.Current).IsEqualTo(0);
        await Assert.That(health.Max).IsEqualTo(0);
        await Assert.That(_world.GetComponent<TestPosition>(survivor).X).IsEqualTo((float)expectedPosition);
        await Assert.That(_world.GetComponent<TestHealth>(survivor).Current).IsEqualTo(expectedHealth);
        await Assert.That(_world.IsAlive(survivor)).IsTrue();
        await Assert.That(_world.IsAlive(replacement)).IsTrue();
        await Assert.That(_world.EntityCount).IsEqualTo(2);
    }

    [Test]
    public async Task Mask_PartialInitializationLeavesOtherFieldsAtZero()
    {
        var mask = MaskOf(TestPosition.TypeId, TestVelocity.TypeId);
        _world.CreateEntity(in mask);
        var removed = _world.CreateEntity(EntityBuilder.Create()
            .Add(new TestPosition { X = 1, Y = 2, Z = 3 })
            .Add(new TestVelocity { X = 4, Y = 5, Z = 6 }));
        _world.Despawn(removed);

        var replacement = _world.CreateEntity(in mask);
        _world.GetComponent<TestPosition>(replacement).X = 42;
        var position = _world.GetComponent<TestPosition>(replacement);
        var velocity = _world.GetComponent<TestVelocity>(replacement);

        await Assert.That(position.X).IsEqualTo(42f);
        await Assert.That(position.Y).IsEqualTo(0f);
        await Assert.That(position.Z).IsEqualTo(0f);
        await Assert.That(velocity.X).IsEqualTo(0f);
        await Assert.That(velocity.Y).IsEqualTo(0f);
        await Assert.That(velocity.Z).IsEqualTo(0f);
    }

    [Test]
    public async Task Mask_RecycledChunkStartsWithZeroComponents()
    {
        var mask = MaskOf(TestPosition.TypeId);
        var first = _world.CreateEntity(in mask);
        var archetype = _world.ArchetypeRegistry.GetById(_world.GetLocation(first).ArchetypeId)!;
        int capacity = archetype.Layout.EntitiesPerChunk;
        for (int i = 1; i < capacity; i++)
            _world.CreateEntity(in mask);

        var removed = _world.CreateEntity(EntityBuilder.Create()
            .Add(new TestPosition { X = 11, Y = 12, Z = 13 }));
        _world.Despawn(removed);
        var replacement = _world.CreateEntity(in mask);
        var position = _world.GetComponent<TestPosition>(replacement);

        await Assert.That(position.X).IsEqualTo(0f);
        await Assert.That(position.Y).IsEqualTo(0f);
        await Assert.That(position.Z).IsEqualTo(0f);
        await Assert.That(_world.EntityCount).IsEqualTo(capacity + 1);
    }

    [Test]
    public async Task Mask_ZeroSizeComponentsDoNotOverwriteEntityIds()
    {
        var mask = MaskOf(TestPosition.TypeId, TestTag.TypeId);
        var first = _world.CreateEntity(in mask);
        var second = _world.CreateEntity(in mask);
        var archetype = _world.ArchetypeRegistry.GetById(_world.GetLocation(second).ArchetypeId)!;
        var chunk = archetype.GetChunk(0);

        await Assert.That(archetype.GetEntityId(chunk, 0)).IsEqualTo(first.Id);
        await Assert.That(archetype.GetEntityId(chunk, 1)).IsEqualTo(second.Id);
        await Assert.That(_world.HasComponent<TestTag>(second)).IsTrue();
    }

    [Test]
    public async Task Mask_EqualMasksShareAnArchetype()
    {
        // The property that keeps a per-object loader from fragmenting the world: two objects
        // authored the same way cost one archetype, not two. Order of Set() must not matter either
        // — a mask is a set, and the registry keys on its value.
        var first = _world.CreateEntity(MaskOf(TestPosition.TypeId, TestVelocity.TypeId));
        var second = _world.CreateEntity(MaskOf(TestVelocity.TypeId, TestPosition.TypeId));

        var firstArchetype = _world.EntityManager.GetLocation(first.Id).ArchetypeId;
        var secondArchetype = _world.EntityManager.GetLocation(second.Id).ArchetypeId;
        await Assert.That(firstArchetype).IsEqualTo(secondArchetype);
    }

    [Test]
    public async Task Mask_MatchesTheArchetypeABuilderWouldHaveProduced()
    {
        // The two overloads are one mechanism reached two ways, and this is what says so: an
        // entity built from a mask and one built from the equivalent builder are archetype
        // siblings. Without it the mask path could quietly intern a parallel archetype carrying
        // the same components, which nothing but a query count would ever notice.
        var built = _world.CreateEntity(
            EntityBuilder.Create()
                .Add(new TestPosition { X = 1 })
                .Add(new TestVelocity { Y = 2 }));
        var masked = _world.CreateEntity(MaskOf(TestPosition.TypeId, TestVelocity.TypeId));

        await Assert.That(_world.EntityManager.GetLocation(masked.Id).ArchetypeId)
            .IsEqualTo(_world.EntityManager.GetLocation(built.Id).ArchetypeId);
    }

    [Test]
    public async Task EmptyMask_BehavesLikeSpawn()
    {
        var masked = _world.CreateEntity(SmallBitSet<ulong>.Empty);
        var spawned = _world.Spawn();

        await Assert.That(_world.IsAlive(masked)).IsTrue();
        await Assert.That(_world.EntityManager.GetLocation(masked.Id).ArchetypeId)
            .IsEqualTo(_world.EntityManager.GetLocation(spawned.Id).ArchetypeId);
    }
}
