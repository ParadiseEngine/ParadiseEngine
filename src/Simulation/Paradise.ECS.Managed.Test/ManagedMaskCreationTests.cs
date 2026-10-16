namespace Paradise.ECS.Managed.Test;

public sealed class ManagedMaskCreationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MaskReuseClearsUnmanagedValuesAndManagedHandles(bool removeFirst)
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var mask = ComponentMask.Empty.Set(RuntimePayload.SlotTypeId).Set(RuntimeNumber.TypeId);
        var first = world.CreateEntity(in mask);
        var last = world.CreateEntity(in mask);
        world.GetComponent<RuntimeNumber>(first).Value = 11;
        world.GetComponent<RuntimeNumber>(last).Value = 22;
        var firstPayload = new RuntimePayload { Value = 1 };
        var lastPayload = new RuntimePayload { Value = 2 };
        world.SetManaged(first, firstPayload);
        world.SetManaged(last, lastPayload);
        world.AddTag<RuntimeTag>(first);
        world.AddTag<RuntimeTag>(last);
        var survivor = removeFirst ? last : first;

        world.Despawn(removeFirst ? first : last);
        var replacement = world.CreateEntity(in mask);

        await Assert.That(world.GetComponent<RuntimeNumber>(replacement).Value).IsEqualTo(0);
        await Assert.That(world.GetManaged<RuntimePayload>(replacement)).IsNull();
        await Assert.That(world.HasTag<RuntimeTag>(replacement)).IsFalse();
        await Assert.That(world.GetComponent<RuntimeNumber>(survivor).Value).IsEqualTo(removeFirst ? 22 : 11);
        await Assert.That(ReferenceEquals(world.GetManaged<RuntimePayload>(survivor), removeFirst ? lastPayload : firstPayload)).IsTrue();
        await Assert.That(world.HasTag<RuntimeTag>(survivor)).IsTrue();
    }
}
