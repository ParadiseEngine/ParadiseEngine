[assembly: Paradise.ECS.SnapshotReadSystems]

namespace Paradise.ECS.Managed.Test;

[ManagedComponent(Snapshot = ManagedSnapshot.Clone)]
public sealed partial class ManagedSystemValue : IManagedClone<ManagedSystemValue>
{
    public int Value { get; set; }
    public static ManagedSystemValue Clone(ManagedSystemValue source) => new() { Value = source.Value };
}

[Component]
public partial struct ManagedSystemTarget
{
    public Entity Entity;
}

[Component]
public partial struct ManagedSystemObservation
{
    public int Previous;
    public int Current;
}

[Queryable(Singleton = true), With<ManagedSystemTarget>(IsReadOnly = true)]
public readonly ref partial struct ManagedSystemTargetQuery;

[Queryable, WithManaged<ManagedSystemValue>]
public readonly ref partial struct ManagedSystemPresenceQuery;

public ref partial struct ManagedSystemFirstWriter : IWorldSystem
{
    public ManagedSystemTargetQuery.Singleton Target;
    public ManagedLookup<ManagedSystemValue> Values;

    public void Execute()
    {
        var entity = Target.ManagedSystemTarget.Entity;
        Values[entity] = new ManagedSystemValue { Value = Values[entity]!.Value + 1 };
    }
}

public ref partial struct ManagedSystemSecondWriter : IWorldSystem
{
    public ManagedSystemTargetQuery.Singleton Target;
    public ManagedLookup<ManagedSystemValue> Values;

    public void Execute()
    {
        var entity = Target.ManagedSystemTarget.Entity;
        Values[entity] = new ManagedSystemValue { Value = Values[entity]!.Value + 10 };
    }
}

[WithManaged<ManagedSystemValue>]
public ref partial struct ManagedSystemSnapshotReader : IEntitySystem
{
    public Entity Entity;
    public ref ManagedSystemObservation Observation;
    public ReadOnlyManagedLookup<ManagedSystemValue> Previous;
    [CurrentTick] public ReadOnlyManagedLookup<ManagedSystemValue> Current;

    public void Execute()
    {
        Observation.Previous = Previous[Entity]!.Value;
        Observation.Current = Current[Entity]!.Value;
    }
}

[WithManaged<ManagedSystemValue>]
public ref partial struct ManagedSystemChunkReader : IChunkSystem
{
    public ReadOnlySpan<Entity> Entities;
    public Span<ManagedSystemObservation> Observations;
    public ReadOnlyManagedLookup<ManagedSystemValue> Previous;
    [CurrentTick] public ReadOnlyManagedLookup<ManagedSystemValue> Current;

    public void ExecuteChunk()
    {
        for (int i = 0; i < Entities.Length; i++)
        {
            Observations[i].Previous = Previous[Entities[i]]!.Value;
            Observations[i].Current = Current[Entities[i]]!.Value;
        }
    }
}

public ref partial struct ManagedSystemDeferredSpawn : IWorldSystem
{
    public EntityCommandBuffer Commands;

    public void Execute()
    {
        var entity = Commands.Spawn();
        Commands.AddManaged(entity, new ManagedSystemValue { Value = 12 });
        Commands.SetManaged(entity, new ManagedSystemValue { Value = 25 });
    }
}

public sealed class ManagedSystemTests
{
    [Test]
    public async Task InjectedCommandBuffer_ReplaysManagedSpawnAddSetInOrderAfterWaves()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        using var schedule = SystemSchedule.Create()
            .AddWorld<ManagedSystemDeferredSpawn>()
            .Build<ParallelWaveScheduler>();

        schedule.Run(world);
        await Assert.That(world.EntityCount).IsEqualTo(1);
        await Assert.That(world.GetManaged<ManagedSystemValue>(world.GetEntity(0))!.Value).IsEqualTo(25);

        schedule.Run(world);
        await Assert.That(world.EntityCount).IsEqualTo(2);
        await Assert.That(world.GetManaged<ManagedSystemValue>(world.GetEntity(1))!.Value).IsEqualTo(25);
        await Assert.That(world.GetManaged<ManagedSystemValue>(world.GetEntity(0))!.Value).IsEqualTo(25);
    }

    [Test]
    public async Task ManagedOnlyWriteConflict_SeparatesWavesAndRunsBothSystems()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var entity = world.Spawn();
        world.AddComponent(entity, new ManagedSystemTarget { Entity = entity });
        world.AddManaged(entity, new ManagedSystemValue { Value = 5 });

        var first = Metadata<ManagedSystemFirstWriter>();
        var second = Metadata<ManagedSystemSecondWriter>();
        var waves = new SnapshotDagScheduler().ComputeWaves<ComponentMask>([first, second]);

        await Assert.That(first.WriteMask.PopCount()).IsEqualTo(1);
        await Assert.That(first.WriteMask.Get(ManagedSystemValue.SlotTypeId)).IsTrue();
        await Assert.That(waves.Length).IsEqualTo(2);

        using var schedule = SystemSchedule.Create()
            .AddWorld<ManagedSystemFirstWriter>()
            .AddWorld<ManagedSystemSecondWriter>()
            .Build(new SnapshotDagScheduler(), new ParallelWaveScheduler());
        schedule.Run(world);

        await Assert.That(world.GetManaged<ManagedSystemValue>(entity)!.Value).IsEqualTo(16);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SnapshotReader_ResolvesClonedReadStoreAndCurrentTickWriteStore(bool chunkSystem)
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var previous = shared.CreateWorld();
        var entities = new Entity[2000];
        for (int i = 0; i < entities.Length; i++)
        {
            var entity = world.Spawn();
            world.AddComponent(entity, new ManagedSystemObservation());
            world.AddManaged(entity, new ManagedSystemValue { Value = i });
            entities[i] = entity;
        }
        previous.CopyFrom(world);
        for (int i = 0; i < entities.Length; i++)
            world.GetManaged<ManagedSystemValue>(entities[i])!.Value = i + 100;

        using var schedule = chunkSystem
            ? SystemSchedule.Create().Add<ManagedSystemChunkReader>().Build<ParallelWaveScheduler>()
            : SystemSchedule.Create().Add<ManagedSystemSnapshotReader>().Build<ParallelWaveScheduler>();
        schedule.Run(world, previous);

        for (int i = 0; i < entities.Length; i++)
        {
            var observed = world.GetComponent<ManagedSystemObservation>(entities[i]);
            await Assert.That(observed.Previous).IsEqualTo(i);
            await Assert.That(observed.Current).IsEqualTo(i + 100);
        }
    }

    [Test]
    public async Task CurrentTickManagedReader_IsOrderedAfterWriter()
    {
        var writer = Metadata<ManagedSystemFirstWriter>();
        var reader = EntityMetadata<ManagedSystemSnapshotReader>();
        var waves = new SnapshotDagScheduler().ComputeWaves<ComponentMask>([reader, writer]);

        await Assert.That(waves.Length).IsEqualTo(2);
        await Assert.That(waves[0]).IsEquivalentTo([1]);
        await Assert.That(waves[1]).IsEquivalentTo([0]);
    }

    [Test]
    public async Task ManagedPresence_DeclaresNullSlotAndMatchesTheArchetype()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var mask = ComponentMask.Empty;
        ManagedSystemPresenceQuery.CollectComponentTypes(ref mask);
        var present = world.CreateEntity(mask);
        world.Spawn();

        await Assert.That(world.HasManaged<ManagedSystemValue>(present)).IsTrue();
        await Assert.That(world.GetManaged<ManagedSystemValue>(present)).IsNull();
        int matches = 0;
        foreach (var _ in world.Query(default(ManagedSystemPresenceQuery)))
            matches++;
        await Assert.That(matches).IsEqualTo(1);
    }

    private static SystemMetadata<ComponentMask> Metadata<T>()
        where T : IWorldSystemRunner<ComponentMask, DefaultConfig>, allows ref struct => T.Metadata;

    private static SystemMetadata<ComponentMask> EntityMetadata<T>()
        where T : ISystem<ComponentMask, DefaultConfig>, allows ref struct => T.Metadata;
}
