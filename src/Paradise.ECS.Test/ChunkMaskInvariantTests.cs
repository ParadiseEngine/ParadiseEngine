namespace Paradise.ECS.Test;

/// <summary>
/// The invariant chunk-level tag skipping would rest on:
///
/// <b>a chunk's tag mask covers every tag carried by the entities in that chunk.</b>
///
/// It only has to be a SUPERSET — the mask is sticky, so bits linger after a tag is removed, and an
/// extra bit merely costs a scan. A MISSING bit is the dangerous direction: a consumer that skips a
/// chunk on a clear bit would step over entities that really do carry the tag, and they would
/// simply stop appearing in queries.
///
/// Nothing reads the mask for filtering yet, so a violation is currently invisible — the row filter
/// still inspects every entity and gets the right answer. These tests pin the invariant NOW,
/// before anything depends on it, and each one names the operation that breaks it.
/// </summary>
public sealed class ChunkMaskInvariantTests : IDisposable
{
    private static readonly DefaultConfig s_config = new();
    private readonly ChunkManager _chunkManager = ChunkManager.Create(s_config);
    private readonly SharedArchetypeMetadata _sharedMetadata = new(ComponentRegistry.Shared.TypeInfos, s_config);
    private readonly World _world;

    public ChunkMaskInvariantTests()
    {
        _world = new World(s_config, _chunkManager, _sharedMetadata);
    }

    public void Dispose()
    {
        _sharedMetadata.Dispose();
        _chunkManager.Dispose();
    }

    /// <summary>Whether this entity's chunk admits to holding the tags the entity actually has.</summary>
    private bool ChunkMaskCovers(Entity entity) =>
        _world.GetChunkMask(entity).ContainsAll(_world.GetTags(entity));

    [Test]
    public async Task TaggingAnEntityCoversIt()
    {
        // The baseline: the one path that does maintain the mask.
        var entity = _world.Spawn();
        _world.AddTag<TestIsPlayer>(entity);

        await Assert.That(ChunkMaskCovers(entity)).IsTrue();
    }

    [Test]
    public async Task AddingAComponentToATaggedEntityKeepsItCovered()
    {
        // Adding a component CHANGES ARCHETYPE, which moves the entity into a different chunk. Its
        // EntityTags component travels with it — but the destination chunk's mask never hears
        // about the tags that just arrived.
        var entity = _world.Spawn();
        _world.AddTag<TestIsPlayer>(entity);

        _world.AddComponent(entity, new TestPosition { X = 1, Y = 2 });

        await Assert.That(ChunkMaskCovers(entity)).IsTrue();
    }

    [Test]
    public async Task RemovingAComponentFromATaggedEntityKeepsItCovered()
    {
        // The same move in the other direction.
        var entity = _world.Spawn();
        _world.AddComponent(entity, new TestPosition { X = 1, Y = 2 });
        _world.AddTag<TestIsPlayer>(entity);

        _world.RemoveComponent<TestPosition>(entity);

        await Assert.That(ChunkMaskCovers(entity)).IsTrue();
    }

    [Test]
    public async Task DespawningPullsAnEntityIntoAnotherChunkAndKeepsItCovered()
    {
        // Despawn swap-removes: the archetype's LAST entity is moved into the hole. When the hole
        // is in an earlier chunk, that entity crosses chunks — carrying tags the destination chunk
        // has never seen. Nothing observes the move, so nothing updates the mask.
        //
        // Enough entities to span several chunks; the exact capacity is a layout detail.
        var entities = new List<Entity>();
        for (var i = 0; i < 3000; i++)
        {
            var e = _world.Spawn();
            _world.AddComponent(e, new TestPosition { X = i, Y = 0 });
            entities.Add(e);
        }

        // A tag only the LAST entity carries, so the first chunk's mask cannot already contain it.
        var last = entities[^1];
        _world.AddTag<TestIsEnemy>(last);
        await Assert.That(ChunkMaskCovers(last)).IsTrue();

        // Open a hole at the very front; `last` is swapped into it.
        _world.Despawn(entities[0]);

        await Assert.That(ChunkMaskCovers(last)).IsTrue();
    }

    [Test]
    public async Task ATaggedEntityStaysFindableAfterMoving()
    {
        // The invariant above, stated as the symptom a player would report. This is the test with
        // teeth now that chunk skipping is live: the query consults the destination chunk's mask
        // before reading any row, so an entity whose move went unrecorded is not merely mis-summarised
        // — it is gone from the query entirely.
        var entity = _world.Spawn();
        _world.AddComponent(entity, new TestPosition { X = 1, Y = 0 });
        _world.AddTag<TestIsPlayer>(entity);

        _world.AddComponent(entity, new TestHealth { Current = 10, Max = 10 });

        var found = 0;
        foreach (var _ in TestTaggedPosition.Query<World, ComponentMask, DefaultConfig>(_world)) found++;
        await Assert.That(found).IsEqualTo(1);
    }

    [Test]
    public async Task AnEntitySwappedIntoAnotherChunkStaysFindable()
    {
        // The despawn hazard as a query result. The entity nobody named — the one the swap-remove
        // dragged forward — has to survive the skip, and it is the case with no obvious place to
        // notice the move from.
        var entities = new List<Entity>();
        for (var i = 0; i < 3000; i++)
        {
            var e = _world.Spawn();
            _world.AddComponent(e, new TestPosition { X = i, Y = 0 });
            entities.Add(e);
        }

        var last = entities[^1];
        _world.AddTag<TestIsPlayer>(last);
        _world.Despawn(entities[0]);

        var found = 0;
        foreach (var _ in TestTaggedPosition.Query<World, ComponentMask, DefaultConfig>(_world)) found++;
        await Assert.That(found).IsEqualTo(1);
    }

    [Test]
    public async Task AChunkWithNoMatchingTagYieldsNothing()
    {
        // The skip's own correctness, from the other side: many chunks, one tagged entity, and the
        // query must return exactly it. A skip that were too eager would return none; one that
        // ignored the mask would still return one — so this passes either way and exists to pin
        // that enabling the coarse pass did not change the ANSWER, only the work.
        var entities = new List<Entity>();
        for (var i = 0; i < 3000; i++)
        {
            var e = _world.Spawn();
            _world.AddComponent(e, new TestPosition { X = i, Y = 0 });
            entities.Add(e);
        }
        _world.AddTag<TestIsPlayer>(entities[^1]);

        var found = new List<float>();
        foreach (var row in TestTaggedPosition.Query<World, ComponentMask, DefaultConfig>(_world))
        {
            found.Add(row.TestPosition.X);
        }

        await Assert.That(found).IsEquivalentTo(new List<float> { 2999f });
    }
}
