namespace Paradise.ECS.Test;

public sealed class ComponentWriterTests
{
    private static void Initialize<TWriter>(TWriter writer, Entity entity, float x)
        where TWriter : IComponentWriter
    {
        writer.AddComponent(entity, new TestPosition { X = x, Y = 3 });
        writer.AddComponent<TestVelocity>(entity);
    }

    [Test]
    public async Task SameInitializerSupportsPlainWorldAndDeferredPlaceholder()
    {
        using var shared = new SharedWorld<SmallBitSet<ulong>, DefaultConfig>(ComponentRegistry.Shared.TypeInfos);
        var world = shared.CreateWorld();
        var immediate = world.Spawn();
        Initialize(world, immediate, 7);
        using var commands = new EntityCommandBuffer();
        var pending = commands.Spawn();
        Initialize(commands, pending, 11);

        await Assert.That(world.EntityCount).IsEqualTo(1);
        await Assert.That(world.GetComponent<TestPosition>(immediate).X).IsEqualTo(7f);
        commands.Playback(world);
        var deferred = commands.Resolve(pending);
        await Assert.That(world.GetComponent<TestPosition>(deferred).X).IsEqualTo(11f);
        await Assert.That(world.GetComponent<TestVelocity>(deferred).X).IsEqualTo(0f);
        await Assert.That(world.GetComponent<TestVelocity>(immediate).X).IsEqualTo(0f);
    }

    [Test]
    public async Task TaggedWorldAndWorldInterfacesExposeTheSameWriter()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var concrete = world.Spawn();
        Initialize(world, concrete, 1);
        var nonGeneric = world.Spawn();
        Initialize<IWorld>(world, nonGeneric, 2);
        var generic = world.Spawn();
        Initialize<IWorld<ComponentMask, DefaultConfig>>(world, generic, 3);
        var narrow = world.Spawn();
        Initialize<IComponentWriter>(world, narrow, 4);
        await Assert.That(world.GetComponent<TestPosition>(concrete).X).IsEqualTo(1f);
        await Assert.That(world.GetComponent<TestPosition>(nonGeneric).X).IsEqualTo(2f);
        await Assert.That(world.GetComponent<TestPosition>(generic).X).IsEqualTo(3f);
        await Assert.That(world.GetComponent<TestPosition>(narrow).X).IsEqualTo(4f);
        world.AddTag<TestIsPlayer>(narrow);
        await Assert.That(world.HasTag<TestIsPlayer>(narrow)).IsTrue();
    }

    [Test]
    public async Task BufferWriterDefersExistingEntityChangesUntilPlayback()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var entity = world.Spawn();
        using var commands = new EntityCommandBuffer();
        Initialize<IComponentWriter>(commands, entity, 5);
        await Assert.That(world.HasComponent<TestPosition>(entity)).IsFalse();
        commands.Playback(world);
        await Assert.That(world.GetComponent<TestPosition>(entity).X).IsEqualTo(5f);
    }

    [Test]
    public async Task ExistingExplicitWorldImplementationInheritsWriterDispatch()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var entity = world.Spawn();
        Initialize(new ExplicitWorld(world), entity, 13);
        await Assert.That(world.GetComponent<TestPosition>(entity).X).IsEqualTo(13f);
    }

    // Models an existing external world whose AddComponent declaration predates IComponentWriter.
    private sealed class ExplicitWorld(IWorld inner) : IWorld
    {
        public Entity Spawn() => inner.Spawn();
        public bool Despawn(Entity entity) => inner.Despawn(entity);
        public bool IsAlive(Entity entity) => inner.IsAlive(entity);
        public int EntityCount => inner.EntityCount;
        void IWorld.AddComponent<T>(Entity entity, T value) => inner.AddComponent(entity, value);
        public void RemoveComponent<T>(Entity entity) where T : unmanaged, IComponent => inner.RemoveComponent<T>(entity);
        public ref T GetComponent<T>(Entity entity) where T : unmanaged, IComponent => ref inner.GetComponent<T>(entity);
        public bool HasComponent<T>(Entity entity) where T : unmanaged, IComponent => inner.HasComponent<T>(entity);
        public bool TryGetComponent<T>(Entity entity, out T value) where T : unmanaged, IComponent => inner.TryGetComponent(entity, out value);
        public bool TrySetComponent<T>(Entity entity, T value) where T : unmanaged, IComponent => inner.TrySetComponent(entity, value);
    }

}
