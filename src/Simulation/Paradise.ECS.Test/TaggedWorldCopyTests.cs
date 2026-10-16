namespace Paradise.ECS.Test;

/// <summary>Snapshot copies preserve entity tags and chunk masks without changing unrelated worlds.</summary>
public sealed class TaggedWorldCopyTests : IDisposable
{
    private static readonly DefaultConfig s_config = new();
    private readonly ChunkManager _chunkManager = ChunkManager.Create(s_config);
    private readonly SharedArchetypeMetadata _sharedMetadata = new(ComponentRegistry.Shared.TypeInfos, s_config);
    private readonly World _source;
    private readonly World _destination;

    public TaggedWorldCopyTests()
    {
        // Both from the same shared resources, exactly as a snapshot pool is.
        _source = new World(s_config, _chunkManager, _sharedMetadata);
        _destination = new World(s_config, _chunkManager, _sharedMetadata);
    }

    public void Dispose()
    {
        _sharedMetadata.Dispose();
        _chunkManager.Dispose();
    }

    private static Entity SpawnTagged(World world, float x)
    {
        var entity = world.Spawn();
        world.AddComponent(entity, new TestPosition { X = x, Y = 0 });
        world.AddTag<TestIsPlayer>(entity);
        return entity;
    }

    private static int CountTagged(World world)
    {
        var count = 0;
        foreach (var _ in TestTaggedPosition.Query<World, ComponentMask, DefaultConfig>(world)) count++;
        return count;
    }

    [Test]
    public async Task TagsSurviveTheCopy()
    {
        var entity = SpawnTagged(_source, 1);

        _destination.CopyFrom(_source);

        // CopyFrom preserves entity IDs and versions, so the source handle addresses its copied entity.
        await Assert.That(_destination.HasTag<TestIsPlayer>(entity)).IsTrue();
    }

    [Test]
    public async Task TagFilteredQueriesWorkOnTheCopy()
    {
        SpawnTagged(_source, 1);
        var untagged = _source.Spawn();
        _source.AddComponent(untagged, new TestPosition { X = 2, Y = 0 });

        _destination.CopyFrom(_source);

        await Assert.That(CountTagged(_destination)).IsEqualTo(1);
    }

    [Test]
    public async Task TheCopysChunkMasksArriveWithItRatherThanBlank()
    {
        SpawnTagged(_source, 1);

        _destination.CopyFrom(_source);

        // This fixture has one actual tag and no stale bits; a blank copied chunk mask would
        // make the population-count difference negative and hide the row from chunk filtering.
        await Assert.That(_destination.ComputeStaleBitStatistics().TotalStaleBits).IsEqualTo(0);
    }

    [Test]
    public async Task StaleBitsAreInheritedByTheCopy()
    {
        // Copy semantics, stated rather than discovered. Masks are sticky — RemoveTag clears the
        // entity's bit and leaves the chunk's — so this source carries a bit no entity has, and the
        // copy carries it too. That is faithful and it is safe (an extra bit costs a scan, never a
        // wrong answer); recomputing here would clean the SNAPSHOT while the live world, the one
        // systems query, kept accumulating. RebuildChunkMasks is the tool for that.
        var entity = SpawnTagged(_source, 1);
        _source.RemoveTag<TestIsPlayer>(entity);
        _source.AddTag<TestIsActive>(entity);
        var sourceStale = _source.ComputeStaleBitStatistics().TotalStaleBits;
        await Assert.That(sourceStale).IsGreaterThan(0);

        _destination.CopyFrom(_source);

        await Assert.That(_destination.ComputeStaleBitStatistics().TotalStaleBits)
            .IsEqualTo(sourceStale);
        // And the QUERY is still exact, because rows are tested individually: the stale chunk bit
        // costs a scan, it does not resurrect a tag.
        await Assert.That(CountTagged(_destination)).IsEqualTo(0);
    }

    [Test]
    public async Task CopyingDoesNotWipeTheSourcesChunkMasks()
    {
        SpawnTagged(_source, 1);

        _destination.CopyFrom(_source);

        // Regression from the former shared mask registry: copying a world must not clear
        // the source's masks. Masks now live in each world's own chunks.
        await Assert.That(_source.ComputeStaleBitStatistics().TotalStaleBits).IsEqualTo(0);
        await Assert.That(CountTagged(_source)).IsEqualTo(1);
    }

    [Test]
    public async Task TheCopyIsIndependentOfItsSource()
    {
        var entity = SpawnTagged(_source, 1);
        _destination.CopyFrom(_source);

        _destination.RemoveTag<TestIsPlayer>(entity);

        await Assert.That(_source.HasTag<TestIsPlayer>(entity)).IsTrue();
        await Assert.That(_destination.HasTag<TestIsPlayer>(entity)).IsFalse();
    }

    [Test]
    public async Task CopyingReplacesWhateverTheDestinationHeld()
    {
        // A pooled snapshot world is reused, so it arrives holding the step before last — here two
        // entities where the source has one.
        SpawnTagged(_destination, 98);
        SpawnTagged(_destination, 99);
        SpawnTagged(_source, 1);

        _destination.CopyFrom(_source);

        // Counted rather than probed with the stale HANDLE: ids are allocated per world, so the
        // destination's first old entity and the source's share id 0 and the handle resolves to the copy
        // either way. What "replaced" actually means is that the destination now holds the
        // source's population and nothing else.
        await Assert.That(_destination.EntityCount).IsEqualTo(_source.EntityCount);
        await Assert.That(CountTagged(_destination)).IsEqualTo(1);
    }
}
