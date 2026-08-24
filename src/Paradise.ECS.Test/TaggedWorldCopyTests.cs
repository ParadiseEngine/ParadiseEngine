namespace Paradise.ECS.Test;

/// <summary>
/// <c>TaggedWorld.CopyFrom</c> — the operation that lets a tagged world take part in snapshot
/// execution, where a host publishes each step by copying the stepped world into a pooled twin.
///
/// Two halves, and they fail in opposite directions. Per-entity tags need nothing special (they
/// live in the EntityTags component, which travels with the chunks) — so those tests would pass
/// against a plain <c>World.CopyFrom</c> and exist to pin that it stays true. The CHUNK masks are
/// the interesting half: they are keyed by chunk handle, the copy lands in different chunks, and
/// the registry is SHARED by every world of one shared — which makes the obvious implementation
/// (copy, then <c>RebuildChunkMasks</c>) quietly destructive to worlds that were not involved.
/// </summary>
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

        // Entity handles hold across worlds built from one shared, so the copy is addressable by
        // the same handle — which is what a snapshot consumer relies on.
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

        // Stale bits are (mask bits − actual bits), so this goes NEGATIVE when a chunk mask is
        // MISSING bits its entities really carry — which is what a copy that ignored the registry
        // would leave, since the destination's chunks are new chunks with no entries. That is the
        // one failure mode that produces wrong answers rather than slow ones: ChunkMayMatch reads a
        // clear bit as proof, so a blank mask makes a consumer skip chunks it must not skip.
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

        // The regression that makes this whole test class worth having. The registry is SHARED,
        // and RebuildChunkMasks() clears ALL of it before recomputing the caller's own chunks —
        // so implementing CopyFrom that way would blank the source's masks on every publish, and
        // a host would silently corrupt the live world's tag bookkeeping once per step by doing
        // nothing worse than taking a snapshot.
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
        // destination's old entity and the source's share id 1 and the handle resolves to the copy
        // either way. What "replaced" actually means is that the destination now holds the
        // source's population and nothing else.
        await Assert.That(_destination.EntityCount).IsEqualTo(_source.EntityCount);
        await Assert.That(CountTagged(_destination)).IsEqualTo(1);
    }
}
