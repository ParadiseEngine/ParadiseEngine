namespace Paradise.ECS.Test;

/// <summary>Mask-created entities reserve tag storage before their first tag is added.</summary>
/// <remarks>The plain-world overload is covered by <see cref="CreateEntityFromMaskTests"/>.</remarks>
public sealed class TaggedCreateEntityFromMaskTests : IDisposable
{
    private static readonly DefaultConfig s_config = new();
    private readonly ChunkManager _chunkManager = ChunkManager.Create(s_config);
    private readonly SharedArchetypeMetadata _sharedMetadata = new(ComponentRegistry.Shared.TypeInfos, s_config);
    private readonly World _world;

    public TaggedCreateEntityFromMaskTests()
    {
        _world = new World(s_config, _chunkManager, _sharedMetadata);
    }

    public void Dispose()
    {
        _sharedMetadata.Dispose();
        _chunkManager.Dispose();
    }

    private static ComponentMask MaskOf<T>() where T : unmanaged, IComponent =>
        default(ComponentMask).Set(T.TypeId);

    [Test]
    public async Task an_entity_built_from_a_bare_mask_can_be_tagged()
    {
        // The mask names ONE ordinary component and says nothing about tags — which is the whole
        // point: the caller does not know EntityTags exists.
        var entity = _world.CreateEntity(MaskOf<TestPosition>());

        _world.AddTag<TestIsPlayer>(entity);

        await Assert.That(_world.HasTag<TestIsPlayer>(entity)).IsTrue();
    }

    [Test]
    public async Task the_tag_storage_is_part_of_the_archetype_before_anything_is_tagged()
    {
        // The fold happens at CREATION, not lazily on the first AddTag. Asserted separately from
        // the round trip above because it is the difference between "tagging works" and "tagging
        // works without moving the entity to another archetype" — and an archetype move would
        // invalidate exactly the handles a snapshot pool hands around.
        var entity = _world.CreateEntity(MaskOf<TestPosition>());

        await Assert.That(_world.HasComponent<EntityTags>(entity)).IsTrue();
        await Assert.That(_world.HasTag<TestIsPlayer>(entity)).IsFalse();
    }

    [Test]
    public async Task a_tagged_mask_entity_is_found_by_a_tag_filtered_query()
    {
        // Through a QUERY, because the chunk mask is a second place the bit has to reach: a tag
        // filter is honoured by iteration, and an entity whose chunk was never told about the tag
        // reads as untagged there while HasTag says otherwise.
        var tagged = _world.CreateEntity(MaskOf<TestPosition>());
        _world.CreateEntity(MaskOf<TestPosition>());
        _world.AddTag<TestIsPlayer>(tagged);

        var found = 0;
        foreach (var _ in TestTaggedPosition.Query<World, ComponentMask, DefaultConfig>(_world))
        {
            found++;
        }

        // ONE of the two. Both satisfy every component the queryable asks for and both were built
        // from the same mask, so a count of 2 would mean the filter saw nothing and a count of 0
        // would mean the tag never reached the chunk.
        await Assert.That(found).IsEqualTo(1);
    }
}
