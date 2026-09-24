namespace Paradise.ECS.Managed.Test;

public sealed class ComponentWriterTests
{
    private static void Initialize<TWriter>(TWriter writer, Entity entity, int value)
        where TWriter : IComponentWriter => writer.AddComponent(entity, new RuntimeNumber { Value = value });

    [Test]
    public async Task WriterUsesManagedWorldStructuralBookkeepingAndSupportsDeferredPlayback()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var entity = world.Spawn();
        var payload = new RuntimePayload { Value = 17 };
        world.AddManaged(entity, payload);
        world.AddTag<RuntimeTag>(entity);
        Initialize(world, entity, 11);
        var other = world.Spawn();
        world.AddManaged(other, new RuntimePayload { Value = 19 });
        using var commands = new EntityCommandBuffer();
        Initialize(commands, other, 23);
        await Assert.That(world.HasComponent<RuntimeNumber>(other)).IsFalse();
        commands.Playback(world);

        await Assert.That(world.GetComponent<RuntimeNumber>(entity).Value).IsEqualTo(11);
        await Assert.That(world.HasTag<RuntimeTag>(entity)).IsTrue();
        await Assert.That(ReferenceEquals(world.GetManaged<RuntimePayload>(entity), payload)).IsTrue();
        await Assert.That(world.GetComponent<RuntimeNumber>(other).Value).IsEqualTo(23);
        await Assert.That(world.GetManaged<RuntimePayload>(other)!.Value).IsEqualTo(19);
        var snapshot = shared.CreateWorld();
        snapshot.CopyFrom(world);
        await Assert.That(snapshot.GetComponent<RuntimeNumber>(other).Value).IsEqualTo(23);
        await Assert.That(ReferenceEquals(snapshot.GetManaged<RuntimePayload>(entity), payload)).IsTrue();
    }
}
