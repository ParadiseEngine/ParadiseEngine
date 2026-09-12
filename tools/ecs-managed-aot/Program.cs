using Paradise.ECS;

[assembly: SnapshotReadSystems]

namespace ManagedAotSmoke;

internal static class Program
{
    public static void Main()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var snapshot = shared.CreateWorld();
        var entity = world.Spawn();
        var reference = new Name { Value = "live" };
        world.AddManaged(entity, reference);
        world.AddManaged(entity, new Counter { Value = 7 });
        world.AddManaged(entity, new Scratch());
        var observer = world.CreateEntity(EntityBuilder.Create().Add(new Observation { Target = entity }));
#if MANAGED_SMOKE_TAGS
        world.AddTag<Selected>(entity);
#endif
        snapshot.CopyFrom(world);
        Require(ReferenceEquals(snapshot.GetManaged<Name>(entity), reference), "Reference snapshot identity");
        world.GetManaged<Counter>(entity)!.Value = 8;
        Require(snapshot.GetManaged<Counter>(entity)!.Value == 7, "Clone snapshot isolation");
        Require(snapshot.HasManaged<Scratch>(entity) && snapshot.GetManaged<Scratch>(entity) is null,
            "Skip preserves presence and clears value");
        using (var schedule = SystemSchedule.Create().Add<ObserveCounter>().Build<SequentialWaveScheduler>())
            schedule.Run(world, snapshot);
        var observation = world.GetComponent<Observation>(observer);
        Require(observation.Previous == 7 && observation.Current == 8, "Generated snapshot and current-tick lookups");
#if MANAGED_SMOKE_TAGS
        Require(snapshot.HasTag<Selected>(entity), "Tag snapshot composition");
#endif

        using var commands = new EntityCommandBuffer();
        var deferred = commands.Spawn();
        commands.AddManaged(deferred, new Name { Value = "first" });
        commands.SetManaged(deferred, new Name { Value = "last" });
#if MANAGED_SMOKE_TAGS
        commands.AddTag<Selected>(deferred);
#endif
        commands.Playback(world);
        var created = commands.Resolve(deferred);
        Require(world.GetManaged<Name>(created)!.Value == "last", "Ordered deferred managed commands");
#if MANAGED_SMOKE_TAGS
        Require(world.HasTag<Selected>(created), "Tag extension chaining");
#endif
        commands.Clear();

        world.Despawn(entity);
        var mask = ComponentMask.Empty;
        Named.CollectComponentTypes(ref mask);
        var reused = world.CreateEntity(in mask);
        Require(world.HasManaged<Name>(reused) && world.GetManaged<Name>(reused) is null,
            "Recycled archetype slots start null");
        snapshot.CopyFrom(world);
        Require(snapshot.GetManaged<Name>(created)!.Value == "last", "Snapshot store follows chunk handles");
        Console.WriteLine("MANAGED-AOT-OK");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

[ManagedComponent]
public sealed partial class Name
{
    public string Value { get; set; } = "";
}

[ManagedComponent(Snapshot = ManagedSnapshot.Clone)]
public sealed partial class Counter : IManagedClone<Counter>
{
    public int Value { get; set; }
    public static Counter Clone(Counter source) => new() { Value = source.Value };
}

[ManagedComponent(Snapshot = ManagedSnapshot.Skip)]
public sealed partial class Scratch;

[Queryable]
[WithManaged<Name>]
public readonly ref partial struct Named;

[Component]
public partial struct Observation
{
    public Entity Target;
    public int Previous;
    public int Current;
}

public ref partial struct ObserveCounter : IEntitySystem
{
    public ref Observation Observation;
    public ReadOnlyManagedLookup<Counter> Previous;
    [CurrentTick] public ReadOnlyManagedLookup<Counter> Current;

    public void Execute()
    {
        Observation.Previous = Previous[Observation.Target]!.Value;
        Observation.Current = Current[Observation.Target]!.Value;
    }
}

#if MANAGED_SMOKE_TAGS
[Tag]
public partial struct Selected;
#endif
