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
        ExerciseWorldEntity(world);
        Console.WriteLine("MANAGED-AOT-OK");
    }

    private static void ExerciseWorldEntity(World world)
    {
        var entity = world.Spawn();
        var access = new WorldEntity(world, entity);
        var stored = new List<WorldEntity> { access };
        Require(ReferenceEquals(access.World, world) && access.Entity == entity && access.IsAlive,
            "WorldEntity binds its world and live entity");

        access.Add<AccessPosition>();
        Require(access.Has<AccessPosition>() && access.Get<AccessPosition>().X == 0,
            "WorldEntity default unmanaged component");
        access.Set(new AccessPosition { X = 10 });
        var copy = access.Get<AccessPosition>();
        copy.X = 99;
        Require(copy.X == 99 && access.Get<AccessPosition>().X == 10, "WorldEntity unmanaged Get returns a copy");
        {
            ref var position = ref access.GetRef<AccessPosition>();
            position.X++;
        }
        Require(access.TryGet<AccessPosition>(out var copied) && copied.X == 11,
            "WorldEntity unmanaged ref and TryGet dispatch");

        access.Add<Name>();
        Require(access.Has<Name>() && access.TryGet<Name>(out var absentName) && absentName is null,
            "WorldEntity default managed component has null value");
        var name = new Name { Value = "bound" };
        access.Set(name);
        access.Add(new Counter { Value = 20 });
        Require(ReferenceEquals(stored[0].Get<Name>(), name) && stored[0].Get<AccessPosition>().X == 11,
            "Stored WorldEntity follows managed archetype moves");
        Require(access.Get<Counter>()!.Value == 20, "WorldEntity managed Add with value");

#if MANAGED_SMOKE_TAGS
        access.Add<Selected>();
        Require(access.Has<Selected>(), "WorldEntity tag Add and Has");
        access.Remove<Selected>();
        Require(!access.Has<Selected>(), "WorldEntity tag Remove");
#endif

        access.Set<Name>(null);
        Require(access.Has<Name>() && access.Get<Name>() is null, "WorldEntity managed Set null preserves presence");
        access.Remove<Name>();
        Require(!access.Has<Name>() && !access.TryGet<Name>(out _), "WorldEntity managed Remove");
        access.Remove<AccessPosition>();
        Require(!access.Has<AccessPosition>() && !access.TryGet<AccessPosition>(out _),
            "WorldEntity unmanaged Remove");
        Require(stored[0].Get<Counter>()!.Value == 20, "Stored WorldEntity follows removal moves");

        access.Despawn();
        var replacement = new WorldEntity(world, world.Spawn());
        replacement.Add(new Counter { Value = 30 });
        Require(!stored[0].IsAlive && !stored[0].Has<Counter>() && !stored[0].TryGet<Counter>(out _),
            "Stored WorldEntity rejects stale generation after entity reuse");
        Require(replacement.IsAlive && replacement.Get<Counter>()!.Value == 30,
            "Replacement entity has independent component state");
        replacement.Despawn();
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

[Component]
public partial struct AccessPosition
{
    public int X;
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
