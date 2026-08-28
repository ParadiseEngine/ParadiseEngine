namespace Paradise.ECS.Test;

/// <summary>
/// Queryables that filter by TAG rather than by archetype — <c>[WithTag&lt;T&gt;]</c> and
/// <c>[WithoutTag&lt;T&gt;]</c>.
///
/// The thing under test is a seam that archetype matching cannot reach: a tag is a bit in the
/// EntityTags component, deliberately not part of the archetype mask, so entities that do and do
/// not carry it sit in the SAME chunk. Everything here is therefore about rows being included or
/// skipped one at a time — including the counts, which cannot come from archetype bookkeeping once
/// a filter is in play, and the singleton, whose "exactly one" has to mean one TAGGED entity.
/// </summary>
public sealed class TagQueryTests : IDisposable
{
    private static readonly DefaultConfig s_config = new();
    private readonly ChunkManager _chunkManager = ChunkManager.Create(s_config);
    private readonly SharedArchetypeMetadata _sharedMetadata = new(ComponentRegistry.Shared.TypeInfos, s_config);
    private readonly World _world;

    public TagQueryTests()
    {
        _world = new World(s_config, _chunkManager, _sharedMetadata);
    }

    public void Dispose()
    {
        _sharedMetadata.Dispose();
        _chunkManager.Dispose();
    }

    /// <summary>An entity with the components the tag queryables want, untagged.</summary>
    private Entity SpawnPositioned(float x)
    {
        var entity = _world.Spawn();
        _world.AddComponent(entity, new TestPosition { X = x, Y = 0 });
        return entity;
    }

    private static int Count(QueryResult<TestTaggedPosition.Data<ComponentMask, DefaultConfig>,
        Archetype<ComponentMask, DefaultConfig>, ComponentMask, DefaultConfig> query)
    {
        var count = 0;
        foreach (var _ in query) count++;
        return count;
    }

    private QueryResult<TestTaggedPosition.Data<ComponentMask, DefaultConfig>,
        Archetype<ComponentMask, DefaultConfig>, ComponentMask, DefaultConfig> TaggedQuery()
        => TestTaggedPosition.Query<World, ComponentMask, DefaultConfig>(_world);

    [Test]
    public async Task AnUntaggedEntityIsNotMatched()
    {
        SpawnPositioned(1);

        // It satisfies every COMPONENT the queryable asks for. Only the tag is missing, and that
        // is the whole distinction this feature exists to draw.
        await Assert.That(Count(TaggedQuery())).IsEqualTo(0);
    }

    [Test]
    public async Task OnlyTaggedEntitiesAreMatched()
    {
        var tagged = SpawnPositioned(1);
        SpawnPositioned(2);
        var alsoTagged = SpawnPositioned(3);
        _world.AddTag<TestIsPlayer>(tagged);
        _world.AddTag<TestIsPlayer>(alsoTagged);

        var seen = new List<float>();
        foreach (var row in TaggedQuery()) seen.Add(row.TestPosition.X);

        // 1 and 3, not 2 — and note all three live in one chunk, so this is a row-level skip
        // rather than an archetype that failed to match.
        await Assert.That(seen).IsEquivalentTo(new List<float> { 1f, 3f });
    }

    [Test]
    public async Task RemovingTheTagRemovesTheEntityFromTheQuery()
    {
        var entity = SpawnPositioned(1);
        _world.AddTag<TestIsPlayer>(entity);
        await Assert.That(Count(TaggedQuery())).IsEqualTo(1);

        _world.RemoveTag<TestIsPlayer>(entity);

        // No archetype moved: the entity is where it was, carrying what it carried. That is the
        // property tags exist for, and the reason the query has to look at rows.
        await Assert.That(Count(TaggedQuery())).IsEqualTo(0);
    }

    [Test]
    public async Task ADifferentTagDoesNotMatch()
    {
        var entity = SpawnPositioned(1);
        _world.AddTag<TestIsEnemy>(entity);

        await Assert.That(Count(TaggedQuery())).IsEqualTo(0);
    }

    [Test]
    public async Task EveryDeclaredTagIsRequired()
    {
        var one = SpawnPositioned(1);
        _world.AddTag<TestIsPlayer>(one);
        var both = SpawnPositioned(2);
        _world.AddTag<TestIsPlayer>(both);
        _world.AddTag<TestIsActive>(both);

        // TestActivePlayer wants both tags: an AND, not an OR.
        var count = 0;
        foreach (var _ in TestActivePlayer.Query<World, ComponentMask, DefaultConfig>(_world)) count++;

        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task CountAndIsEmptyRespectTheFilterWhileCapacityDoesNot()
    {
        SpawnPositioned(1);
        SpawnPositioned(2);

        // Two entities in a matching archetype, none of them tagged. The three members answer three
        // different questions, and the split is the point: capacity is what the ARCHETYPES hold
        // (cheap, and an upper bound), Count() is what iteration will actually yield, IsEmpty is
        // whether that yield is anything at all.
        await Assert.That(TaggedQuery().EntityCapacity).IsEqualTo(2);
        await Assert.That(TaggedQuery().Count()).IsEqualTo(0);
        await Assert.That(TaggedQuery().IsEmpty).IsTrue();

        var tagged = SpawnPositioned(3);
        _world.AddTag<TestIsPlayer>(tagged);

        await Assert.That(TaggedQuery().EntityCapacity).IsEqualTo(3);
        await Assert.That(TaggedQuery().Count()).IsEqualTo(1);
        await Assert.That(TaggedQuery().IsEmpty).IsFalse();
    }

    [Test]
    public async Task CapacityEqualsCountWhenNothingFilters()
    {
        // The other half of the contract: for a queryable with no row filter — which is nearly all
        // of them — the bound IS the count, so nothing is lost by the property being a bound.
        SpawnPositioned(1);
        SpawnPositioned(2);

        // Re-queried per assertion: a QueryResult is a ref struct and cannot live across an await.
        await Assert.That(
            TestPositionOnly.Query<World, ComponentMask, DefaultConfig>(_world).EntityCapacity)
            .IsEqualTo(2);
        await Assert.That(
            TestPositionOnly.Query<World, ComponentMask, DefaultConfig>(_world).Count())
            .IsEqualTo(2);
    }

    [Test]
    public async Task AnUnfilteredQueryableIsUnaffected()
    {
        var entity = SpawnPositioned(1);
        _world.AddTag<TestIsPlayer>(entity);
        SpawnPositioned(2);

        // The other half of the contract: [WithTag] is opt-in, and a queryable that declares none
        // still matches on archetype alone — tagged or not, it sees both.
        var count = 0;
        foreach (var _ in TestPositionOnly.Query<World, ComponentMask, DefaultConfig>(_world)) count++;

        await Assert.That(count).IsEqualTo(2);
    }

    // ---------------------------------------------------------------------------------------
    // Singletons — "exactly one" has to mean exactly one TAGGED entity.
    // ---------------------------------------------------------------------------------------

    private TestTaggedSingleton.Singleton<ComponentMask, DefaultConfig> ResolveSingleton()
        => TestTaggedSingleton.Singleton<ComponentMask, DefaultConfig>.Resolve(_world, null);

    [Test]
    public async Task TheSingletonResolvesToTheTaggedEntity()
    {
        // Untagged entities FIRST, so the tagged one is not at index 0 of its chunk. That ordering
        // is the test: the unfiltered singleton binds index 0 of the first non-empty chunk, which
        // here would hand back another entity's components rather than failing.
        SpawnPositioned(1);
        SpawnPositioned(2);
        var target = SpawnPositioned(3);
        _world.AddTag<TestIsPlayer>(target);

        var singleton = ResolveSingleton();

        await Assert.That(singleton.TestPosition.X).IsEqualTo(3f);
    }

    [Test]
    public async Task TheSingletonRefusesWhenNothingIsTagged()
    {
        // Two entities matching every component and carrying no tag: the archetype-level count is
        // two, and the answer must still be zero.
        SpawnPositioned(1);
        SpawnPositioned(2);

        await Assert.That(() => { _ = ResolveSingleton(); }).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task TheSingletonRefusesWhenTwoEntitiesAreTagged()
    {
        var first = SpawnPositioned(1);
        var second = SpawnPositioned(2);
        _world.AddTag<TestIsPlayer>(first);
        _world.AddTag<TestIsPlayer>(second);

        await Assert.That(() => { _ = ResolveSingleton(); }).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task TheSingletonBindsThroughSnapshotPairingAtANonZeroRow()
    {
        // The path review flagged as untested, and the one most likely to regress in silence.
        //
        // A tag-filtered singleton can bind ANY row of a chunk, unlike an unfiltered one which is
        // always row 0. Under snapshot-read execution its read-only components then resolve against
        // the paired chunk in another world at that same index — so both the pairing's bounds check
        // and the captured index have to be right, and neither is exercised by resolving against a
        // live world with readWorld: null.
        //
        // Untagged neighbours first, so the match sits at index 2 rather than 0.
        SpawnPositioned(1);
        SpawnPositioned(2);
        var target = SpawnPositioned(3);
        _world.AddTag<TestIsPlayer>(target);

        var readWorld = new World(s_config, _chunkManager, _sharedMetadata);
        readWorld.CopyFrom(_world);

        // Diverge the WRITE world after the copy. TestPosition is a read-only claim, so a correctly
        // paired singleton reports the READ world's value; binding the wrong index — or the wrong
        // world — shows up as one of the other numbers.
        _world.GetComponent<TestPosition>(target).X = 99f;

        var singleton = TestTaggedSingleton.Singleton<ComponentMask, DefaultConfig>
            .Resolve(_world, readWorld);

        await Assert.That(singleton.TestPosition.X).IsEqualTo(3f);
    }

    [Test]
    public async Task RetaggingMovesTheSingletonWithoutAStructuralChange()
    {
        // The property the whole tag route exists for: WHICH entity the singleton resolves to is a
        // value write, so it can happen on a running simulation. With a marker COMPONENT the same
        // move is an add plus a remove — a structural change, illegal mid-step.
        var first = SpawnPositioned(1);
        var second = SpawnPositioned(2);
        _world.AddTag<TestIsPlayer>(first);
        await Assert.That(ResolveSingleton().TestPosition.X).IsEqualTo(1f);

        _world.RemoveTag<TestIsPlayer>(first);
        _world.AddTag<TestIsPlayer>(second);

        await Assert.That(ResolveSingleton().TestPosition.X).IsEqualTo(2f);
    }

    // ---------------------------------------------------------------------------------------
    // [WithoutTag] — the invert: the row is kept when the bit is clear.
    // ---------------------------------------------------------------------------------------

    private int CountUntagged()
    {
        var count = 0;
        foreach (var _ in TestUntaggedPosition.Query<World, ComponentMask, DefaultConfig>(_world))
            count++;
        return count;
    }

    [Test]
    public async Task WithoutTag_SkipsTheTaggedEntity()
    {
        var tagged = SpawnPositioned(1);
        SpawnPositioned(2);
        _world.AddTag<TestIsPlayer>(tagged);

        // Same archetype as TestTaggedPosition; the invert yields the untagged neighbour.
        await Assert.That(CountUntagged()).IsEqualTo(1);
        await Assert.That(Count(TaggedQuery())).IsEqualTo(1);
    }

    [Test]
    public async Task WithoutTag_LookupRejectsATaggedHandle()
    {
        var tagged = SpawnPositioned(1);
        var plain = SpawnPositioned(2);
        _world.AddTag<TestIsPlayer>(tagged);

        var lookup = new TestUntaggedPosition.ReadLookup(_world);
        var plainMatches = lookup.Has(plain);
        var taggedMatches = lookup.Has(tagged);
        await Assert.That(plainMatches).IsTrue();
        await Assert.That(taggedMatches).IsFalse();
    }

    [Test]
    public async Task IgnoreTags_LookupAcceptsATaggedHandle()
    {
        var tagged = SpawnPositioned(1);
        var plain = SpawnPositioned(2);
        _world.AddTag<TestIsPlayer>(tagged);

        var lookup = new TestUntaggedPosition.ReadLookup(_world, ignoreTags: true);
        var plainMatches = lookup.Has(plain);
        var taggedMatches = lookup.Has(tagged);
        await Assert.That(plainMatches).IsTrue();
        await Assert.That(taggedMatches).IsTrue();
    }

    [Test]
    public async Task IgnoreTags_SingletonCountsUntagged()
    {
        // One untagged entity: the filter would refuse (zero tagged), ignoreTags binds it.
        SpawnPositioned(1);

        var x = TestTaggedSingleton.Singleton<ComponentMask, DefaultConfig>
            .Resolve(_world, null, ignoreTags: true).TestPosition.X;
        await Assert.That(x).IsEqualTo(1f);
    }

    [Test]
    public async Task IgnoreTags_SingletonRefusesTwoUntagged()
    {
        // Two untagged: ignoreTags counts at archetype level, so this is two, not zero.
        SpawnPositioned(1);
        SpawnPositioned(2);

        await Assert.That(() =>
        {
            _ = TestTaggedSingleton.Singleton<ComponentMask, DefaultConfig>
                .Resolve(_world, null, ignoreTags: true);
        }).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task WithTagAndWithoutTag_AreAnIntersection()
    {
        var player = SpawnPositioned(1);
        var active = SpawnPositioned(2);
        var both = SpawnPositioned(3);
        SpawnPositioned(4);
        _world.AddTag<TestIsPlayer>(player);
        _world.AddTag<TestIsActive>(active);
        _world.AddTag<TestIsPlayer>(both);
        _world.AddTag<TestIsActive>(both);

        var seen = new List<float>();
        foreach (var row in TestActiveNonPlayer.Query<World, ComponentMask, DefaultConfig>(_world))
            seen.Add(row.TestPosition.X);

        // Active and not player: only 2. Untagged 4 is out; player-only 1 is out; both 3 is out.
        await Assert.That(seen).IsEquivalentTo(new List<float> { 2f });
    }
}

// ===== Queryables under test =====
//
// Explicit ids on purpose. Auto-assigned ids are handed out in sorted-FQN order across the auto
// queryables of the assembly, so declaring new ones here would renumber the existing fixtures and
// break QueryableRegistryTests' assertions about them — a fact about the test project, not about
// tags. Manual ids sit outside that pool and leave them alone.

/// <summary>Position, and the player tag. Its component requirement is deliberately identical to
/// <see cref="TestPositionOnly"/>'s so the two differ in nothing but the tag.</summary>
[Queryable(Id = 20)]
[WithTag<TestIsPlayer>]
[With<TestPosition>]
public readonly ref partial struct TestTaggedPosition;

/// <summary>Two tags: both required.</summary>
[Queryable(Id = 21)]
[WithTag<TestIsPlayer>]
[WithTag<TestIsActive>]
[With<TestPosition>]
public readonly ref partial struct TestActivePlayer;

/// <summary>The control: same components, no tags.</summary>
[Queryable(Id = 22)]
[With<TestPosition>]
public readonly ref partial struct TestPositionOnly;

/// <summary>A tagged SINGLETON — the shape a camera-target contract takes.</summary>
[Queryable(Id = 23, Singleton = true)]
[WithTag<TestIsPlayer>]
[With<TestPosition>(IsReadOnly = true)]
public readonly ref partial struct TestTaggedSingleton;

/// <summary>The invert of <see cref="TestTaggedPosition"/>: same components, player tag excluded.</summary>
[Queryable(Id = 24)]
[WithoutTag<TestIsPlayer>]
[With<TestPosition>]
public readonly ref partial struct TestUntaggedPosition;

/// <summary>Active, and not a player — both polarities on one queryable.</summary>
[Queryable(Id = 25)]
[WithTag<TestIsActive>]
[WithoutTag<TestIsPlayer>]
[With<TestPosition>]
public readonly ref partial struct TestActiveNonPlayer;
