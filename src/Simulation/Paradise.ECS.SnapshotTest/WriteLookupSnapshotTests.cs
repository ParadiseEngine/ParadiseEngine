namespace Paradise.ECS.SnapshotTest;

[Queryable]
[With<SnapMarker>]
[With<SnapPosition>(IsReadOnly = true)]
[Optional<SnapExtra>(IsReadOnly = true)]
public readonly ref partial struct SnapMixedLookup;

[Queryable]
[With<SnapArbitrarySource>]
public readonly ref partial struct SnapLookupSources;

public ref partial struct SnapMixedLookupEntitySystem : IEntitySystem
{
    public ref SnapArbitrarySource Source;
    public SnapMixedLookup.WriteLookup Target;

    public void Execute() => SnapMixedLookupAccess.Apply(Target, Source.Target);
}

public ref partial struct SnapMixedLookupChunkSystem : IChunkSystem
{
    public Span<SnapArbitrarySource> Sources;
    public SnapMixedLookup.WriteLookup Target;

    public void ExecuteChunk()
    {
        foreach (var source in Sources)
            SnapMixedLookupAccess.Apply(Target, source.Target);
    }
}

public ref partial struct SnapMixedLookupWorldSystem : IWorldSystem
{
    public SnapLookupSources.Segments Sources;
    public SnapMixedLookup.WriteLookup Target;

    public void Execute()
    {
        for (var i = 0; i < Sources.Length; i++)
            SnapMixedLookupAccess.Apply(Target, Sources.SnapArbitrarySource[i].Target);
    }
}

internal static class SnapMixedLookupAccess
{
    internal static void Apply(SnapMixedLookup.WriteLookup lookup, Entity entity)
    {
        if (lookup.TryGet(entity, out var target))
        {
            target.SnapMarker.Observed += target.SnapPosition.X
                + (target.HasSnapExtra ? target.GetSnapExtra().Value : 0);
        }
    }
}

public sealed class WriteLookupSnapshotTests : IDisposable
{
    private readonly SharedWorld _shared = SharedWorldFactory.Create();
    private readonly World _read;
    private readonly World _write;

    public WriteLookupSnapshotTests()
    {
        _read = _shared.CreateWorld();
        _write = _shared.CreateWorld();
    }

    public void Dispose() => _shared.Dispose();

    [Test]
    [Arguments("entity")]
    [Arguments("chunk")]
    [Arguments("world")]
    public async Task mixed_lookup_reads_snapshot_components_and_writes_current_components(string systemKind)
    {
        var target = _read.Spawn();
        _read.AddComponent(target, new SnapPosition { X = 10f });
        _read.AddComponent(target, new SnapMarker { Observed = 1f });
        _read.AddComponent(target, new SnapExtra { Value = 5 });
        var source = _read.Spawn();
        _read.AddComponent(source, new SnapArbitrarySource { Target = target });
        _write.CopyFrom(_read);
        _write.GetComponent<SnapPosition>(target).X = 99f;
        _write.GetComponent<SnapMarker>(target).Observed = 100f;
        _write.GetComponent<SnapExtra>(target).Value = 50;

        using var schedule = systemKind switch
        {
            "entity" => SystemSchedule.Create().Add<SnapMixedLookupEntitySystem>()
                .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler()),
            "chunk" => SystemSchedule.Create().Add<SnapMixedLookupChunkSystem>()
                .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler()),
            _ => SystemSchedule.Create().AddWorld<SnapMixedLookupWorldSystem>()
                .Build(new SnapshotDagScheduler(), new SequentialWaveScheduler()),
        };
        schedule.Run(_write, _read);

        await Assert.That(_write.GetComponent<SnapMarker>(target).Observed).IsEqualTo(115f);
        await Assert.That(_read.GetComponent<SnapMarker>(target).Observed).IsEqualTo(1f);
    }

    [Test]
    public async Task mixed_lookup_without_snapshot_keeps_live_component_access()
    {
        var target = Seed(_write, 10f);
        SnapMixedLookupAccess.Apply(new SnapMixedLookup.WriteLookup(_write), target);

        await Assert.That(_write.GetComponent<SnapMarker>(target).Observed).IsEqualTo(10f);
    }

    [Test]
    public async Task mixed_lookup_without_a_matching_snapshot_chunk_reads_current_components()
    {
        _write.CopyFrom(_read);
        var target = Seed(_write, 10f);
        SnapMixedLookupAccess.Apply(new SnapMixedLookup.WriteLookup(_write, _read), target);

        await Assert.That(_write.GetComponent<SnapMarker>(target).Observed).IsEqualTo(10f);
    }

    [Test]
    public async Task mixed_lookup_rejects_an_entity_added_to_a_shorter_snapshot_chunk()
    {
        Seed(_read, 10f);
        _write.CopyFrom(_read);
        var target = Seed(_write, 20f);

        await Assert.That(() => SnapMixedLookupAccess.Apply(
            new SnapMixedLookup.WriteLookup(_write, _read), target)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task mixed_lookup_rejects_stale_targets_even_when_the_snapshot_contains_them()
    {
        var target = Seed(_read, 10f);
        _write.CopyFrom(_read);
        _write.Despawn(target);
        var matches = new SnapMixedLookup.WriteLookup(_write, _read).Has(target);

        await Assert.That(matches).IsFalse();
        await Assert.That(_read.GetComponent<SnapMarker>(target).Observed).IsEqualTo(0f);
    }

    [Test]
    public async Task writable_only_lookup_does_not_require_a_snapshot_row()
    {
        Seed(_read, 10f);
        _write.CopyFrom(_read);
        var target = Seed(_write, 20f);
        SetLivePosition(target);

        await Assert.That(_write.GetComponent<SnapPosition>(target).X).IsEqualTo(42f);
    }

    private void SetLivePosition(Entity entity)
    {
        var lookup = new SnapWritableTarget.WriteLookup<ComponentMask, DefaultConfig>(_write, _read);
        if (lookup.TryGet(entity, out var target))
            target.SnapPosition.X = 42f;
    }

    private static Entity Seed(World world, float position)
    {
        var target = world.Spawn();
        world.AddComponent(target, new SnapPosition { X = position });
        world.AddComponent(target, new SnapMarker());
        return target;
    }
}
