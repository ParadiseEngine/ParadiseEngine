namespace Paradise.ECS.Managed.Test;

public sealed class ManagedQueryBuilderTests
{
    [Test]
    public async Task TypedFiltersSetTheManagedSlotBits()
    {
        var typed = QueryBuilder.Create()
            .WithManaged<RuntimePayload>()
            .WithoutManaged<SkipPayload>()
            .WithManagedAny<ClonePayload>()
            .Description;
        var raw = QueryBuilder.Create()
            .With(RuntimePayload.SlotTypeId)
            .Without(SkipPayload.SlotTypeId)
            .WithAny(ClonePayload.SlotTypeId)
            .Description;

        await Assert.That(typed).IsEqualTo(raw);
        await Assert.That(typed.All.Get(RuntimePayload.SlotTypeId)).IsTrue();
        await Assert.That(typed.None.Get(SkipPayload.SlotTypeId)).IsTrue();
        await Assert.That(typed.Any.Get(ClonePayload.SlotTypeId)).IsTrue();
    }

    [Test]
    public async Task WithManagedMatchesPresenceIncludingNullValues()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var named = world.Spawn();
        world.AddManaged(named, new RuntimePayload { Value = 1 });
        var empty = world.Spawn();
        world.AddManaged<RuntimePayload>(empty, null);
        var excluded = world.Spawn();
        world.AddManaged(excluded, new RuntimePayload { Value = 2 });
        world.AddManaged(excluded, new SkipPayload());
        world.AddComponent(world.Spawn(), new RuntimeNumber { Value = 3 });

        var matched = new List<Entity>();
        foreach (var row in QueryBuilder.Create().WithManaged<RuntimePayload>().WithoutManaged<SkipPayload>().Build(world))
            matched.Add(row.Entity);

        await Assert.That(matched).IsEquivalentTo([named, empty]);
    }

    [Test]
    public async Task WithManagedAnyMatchesEitherManagedComponent()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var runtime = world.Spawn();
        world.AddManaged(runtime, new RuntimePayload());
        var clone = world.Spawn();
        world.AddManaged(clone, new ClonePayload());
        world.AddComponent(world.Spawn(), new RuntimeNumber());

        var matched = new List<Entity>();
        foreach (var row in QueryBuilder.Create().WithManagedAny<RuntimePayload>().WithManagedAny<ClonePayload>().Build(world))
            matched.Add(row.Entity);

        await Assert.That(matched).IsEquivalentTo([runtime, clone]);
    }
}
