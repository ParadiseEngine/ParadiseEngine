namespace Paradise.ECS.Test;

/// <summary>
/// Tests for building entities from queryables (<see cref="IComponentSet"/> +
/// <c>EntityBuilder.EnsureFrom</c>).
///
/// The property worth protecting is the one in <see cref="EnsureFrom_EntityIsMatchedByThatQueryable"/>:
/// an entity built from a queryable is matched by it. Hand-listing an archetype's components
/// separately from the queryables that read them is exactly how a query ends up silently matching
/// nothing — no exception, just a system that quietly stops doing anything.
/// </summary>
public sealed class EntityFromQueryableTests : IDisposable
{
    private static readonly DefaultConfig s_config = new();
    private readonly ChunkManager _chunkManager = ChunkManager.Create(s_config);
    private readonly SharedArchetypeMetadata<SmallBitSet<ulong>, DefaultConfig> _sharedMetadata = new(ComponentRegistry.Shared.TypeInfos, s_config);
    private readonly World<SmallBitSet<ulong>, DefaultConfig> _world;

    public EntityFromQueryableTests()
    {
        _world = new World<SmallBitSet<ulong>, DefaultConfig>(s_config, _sharedMetadata, _chunkManager);
    }

    public void Dispose()
    {
        _sharedMetadata.Dispose();
        _chunkManager.Dispose();
    }

    [Test]
    public async Task EnsureFrom_AddsEveryRequiredComponent()
    {
        // TestHealthEntity requires only TestHealth.
        var entity = _world.CreateEntity(EntityBuilder.Create().EnsureFrom<TestHealthEntity>());

        await Assert.That(_world.HasComponent<TestHealth>(entity)).IsTrue();
    }

    [Test]
    public async Task EnsureFrom_ComponentsStartAtTheirDefault()
    {
        var entity = _world.CreateEntity(EntityBuilder.Create().EnsureFrom<TestMovableEntity>());

        // Nothing is written for an ensured component; it reads back as default because chunk
        // memory is zero-initialized. A garbage value here would mean the builder had put the
        // type in the mask without the storage being cleared.
        await Assert.That(_world.GetComponent<TestPosition>(entity).X).IsEqualTo(0f);
        await Assert.That(_world.GetComponent<TestHealth>(entity).Current).IsEqualTo(0);
    }

    [Test]
    public async Task EnsureFrom_EntityIsMatchedByThatQueryable()
    {
        // The whole point: build from the queryable, and the queryable finds it. This is the
        // check that cannot pass by accident if the archetype and the query disagree.
        var entity = _world.CreateEntity(EntityBuilder.Create().EnsureFrom<TestMovableEntity>());

        var matched = 0;
        foreach (var _ in _world.Query(default(TestMovableEntity)))
        {
            matched++;
        }

        await Assert.That(_world.IsAlive(entity)).IsTrue();
        await Assert.That(matched).IsEqualTo(1);
    }

    [Test]
    public async Task EnsureFrom_ExcludesWithoutComponents()
    {
        // TestMovableEntity is [Without<TestVelocity>]. Contributing it would build an entity the
        // queryable could never match — the exact opposite of the feature.
        var entity = _world.CreateEntity(EntityBuilder.Create().EnsureFrom<TestMovableEntity>());

        await Assert.That(_world.HasComponent<TestVelocity>(entity)).IsFalse();
    }

    [Test]
    public async Task EnsureFrom_ExcludesWithAnyComponents()
    {
        // TestProjectile is [WithAny<TestDamage>]. "Any" names no single required component, so
        // the set leaves it out rather than guessing.
        var entity = _world.CreateEntity(EntityBuilder.Create().EnsureFrom<TestProjectile>());

        await Assert.That(_world.HasComponent<TestPosition>(entity)).IsTrue();
        await Assert.That(_world.HasComponent<TestVelocity>(entity)).IsTrue();
        await Assert.That(_world.HasComponent<TestDamage>(entity)).IsFalse();
    }

    [Test]
    public async Task EnsureFrom_ComposesSeveralQueryablesByUnion()
    {
        // The real spawn shape: an entity several systems each claim part of. Overlapping
        // components (TestPosition is in both) must not collide.
        var entity = _world.CreateEntity(EntityBuilder.Create()
            .EnsureFrom<TestHealthEntity>()
            .EnsureFrom<TestProjectile>());

        await Assert.That(_world.HasComponent<TestHealth>(entity)).IsTrue();
        await Assert.That(_world.HasComponent<TestPosition>(entity)).IsTrue();
        await Assert.That(_world.HasComponent<TestVelocity>(entity)).IsTrue();
    }

    [Test]
    public async Task EnsureFrom_AddSeedsAValueOverTheDefault()
    {
        // Build the SHAPE from the queryable, then seed only the components that need a value —
        // the pattern that replaces a hand-written component list.
        var entity = _world.CreateEntity(EntityBuilder.Create()
            .EnsureFrom<TestMovableEntity>()
            .Add(new TestPosition { X = 3, Y = 4, Z = 5 }));

        var position = _world.GetComponent<TestPosition>(entity);
        await Assert.That(position.X).IsEqualTo(3f);
        await Assert.That(position.Y).IsEqualTo(4f);
        await Assert.That(position.Z).IsEqualTo(5f);
        // Still one entity in the archetype, i.e. Add overwrote the default rather than
        // duplicating TestPosition in the mask.
        await Assert.That(_world.HasComponent<TestHealth>(entity)).IsTrue();
    }

    [Test]
    public async Task EnsureFrom_OrderDoesNotMatter()
    {
        var a = _world.CreateEntity(EntityBuilder.Create()
            .Add(new TestPosition { X = 1 })
            .EnsureFrom<TestMovableEntity>());
        var b = _world.CreateEntity(EntityBuilder.Create()
            .EnsureFrom<TestMovableEntity>()
            .Add(new TestPosition { X = 1 }));

        // Both land in the same archetype and keep the seeded value: EnsureFrom only ever ORs
        // type bits, so it cannot clobber a value written by an Add on either side of it.
        await Assert.That(_world.GetComponent<TestPosition>(a).X).IsEqualTo(1f);
        await Assert.That(_world.GetComponent<TestPosition>(b).X).IsEqualTo(1f);
        await Assert.That(_world.HasComponent<TestHealth>(a)).IsTrue();
        await Assert.That(_world.HasComponent<TestHealth>(b)).IsTrue();
    }
}
