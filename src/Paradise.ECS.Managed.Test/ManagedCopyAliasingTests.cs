namespace Paradise.ECS.Managed.Test;

public sealed class ManagedCopyAliasingTests
{
    [Test]
    public async Task PlainInnerAliasDoesNotMutateSource()
    {
        using var shared = new SharedWorld<ComponentMask, DefaultConfig>(ComponentRegistry.Shared.TypeInfos);
        var inner = shared.CreateWorld();

        await AssertAliasedCopyRejected(inner, inner, static (target, source) => target.CopyFrom(source)).ConfigureAwait(false);
    }

    [Test]
    public async Task TaggedInnerAliasDoesNotMutateSource()
    {
        using var shared = new SharedTaggedWorld<ComponentMask, DefaultConfig, EntityTags, TagMask>(ComponentRegistry.Shared.TypeInfos);
        var inner = shared.CreateWorld();

        await AssertAliasedCopyRejected(inner, inner, static (target, source) => target.CopyFrom(source)).ConfigureAwait(false);
    }

    [Test]
    public async Task ValueTypeInnerAliasDoesNotMutateSource()
    {
        using var shared = new SharedWorld<ComponentMask, DefaultConfig>(ComponentRegistry.Shared.TypeInfos);
        var inner = shared.CreateWorld();

        await AssertAliasedCopyRejected(new ForwardingWorld(inner), new ForwardingWorld(inner),
            static (target, source) => target.CopyFrom(source)).ConfigureAwait(false);
    }

    [Test]
    public async Task IndependentValueTypeInnerWorldsSupportSnapshots()
    {
        using var shared = new SharedManagedWorld<ComponentMask, DefaultConfig, ForwardingWorld>(
            ComponentRegistry.Shared.TypeInfos, ManagedRegistry.TypeInfos,
            static (config, chunks, metadata) => new ForwardingWorld(new Paradise.ECS.World<ComponentMask, DefaultConfig>(config, metadata, chunks)),
            static (target, source) => target.CopyFrom(source));
        var source = shared.CreateWorld();
        var target = shared.CreateWorld();
        var entity = source.Spawn();
        var payload = new RuntimePayload { Value = 73 };
        source.AddComponent(entity, new RuntimeNumber { Value = 42 });
        source.AddManaged(entity, payload);
        target.AddManaged(target.Spawn(), new RuntimePayload { Value = -1 });

        target.CopyFrom(source);

        await Assert.That(target.EntityCount).IsEqualTo(1);
        await Assert.That(target.GetComponent<RuntimeNumber>(entity).Value).IsEqualTo(42);
        await Assert.That(ReferenceEquals(target.GetManaged<RuntimePayload>(entity), payload)).IsTrue();
        target.Clear();
        await Assert.That(source.EntityCount).IsEqualTo(1);
        await Assert.That(source.IsAlive(entity)).IsTrue();
        await Assert.That(source.GetComponent<RuntimeNumber>(entity).Value).IsEqualTo(42);
        await Assert.That(ReferenceEquals(source.GetManaged<RuntimePayload>(entity), payload)).IsTrue();
    }

    private static async Task AssertAliasedCopyRejected<TInner>(TInner sourceInner, TInner targetInner,
        Action<TInner, TInner> copyFrom) where TInner : IWorld<ComponentMask, DefaultConfig>
    {
        int copyCalls = 0;
        void Copy(TInner target, TInner source)
        {
            copyCalls++;
            copyFrom(target, source);
        }

        var source = new ManagedWorld<ComponentMask, DefaultConfig, TInner>(sourceInner, ManagedRegistry.TypeInfos, Copy);
        var target = new ManagedWorld<ComponentMask, DefaultConfig, TInner>(targetInner, ManagedRegistry.TypeInfos, Copy);
        var entity = source.Spawn();
        var payload = new RuntimePayload { Value = 73 };
        source.AddComponent(entity, new RuntimeNumber { Value = 42 });
        source.AddManaged(entity, payload);

        await Assert.That(() => target.CopyFrom(source)).Throws<InvalidOperationException>();

        await Assert.That(source.EntityCount).IsEqualTo(1);
        await Assert.That(source.IsAlive(entity)).IsTrue();
        await Assert.That(source.GetComponent<RuntimeNumber>(entity).Value).IsEqualTo(42);
        await Assert.That(ReferenceEquals(source.GetManaged<RuntimePayload>(entity), payload)).IsTrue();
        await Assert.That(payload.Value).IsEqualTo(73);
        await Assert.That(copyCalls).IsEqualTo(0);
    }

    private readonly struct ForwardingWorld(Paradise.ECS.World<ComponentMask, DefaultConfig> inner)
        : IWorld<ComponentMask, DefaultConfig>
    {
        public Entity Spawn() => inner.Spawn();
        public bool Despawn(Entity entity) => inner.Despawn(entity);
        public bool IsAlive(Entity entity) => inner.IsAlive(entity);
        public int EntityCount => inner.EntityCount;
        public void AddComponent<T>(Entity entity, T value = default) where T : unmanaged, IComponent => inner.AddComponent(entity, value);
        public void RemoveComponent<T>(Entity entity) where T : unmanaged, IComponent => inner.RemoveComponent<T>(entity);
        public ChunkManager ChunkManager => inner.ChunkManager;
        public WorldEventStore Events => inner.Events;
        public IEntityManager EntityManager => inner.EntityManager;
        public ArchetypeRegistry<ComponentMask, DefaultConfig> ArchetypeRegistry => inner.ArchetypeRegistry;
        public EntityIdAllocator EntityIdAllocator => inner.EntityIdAllocator;
        public ref T GetComponent<T>(Entity entity) where T : unmanaged, IComponent => ref inner.GetComponent<T>(entity);
        public bool HasComponent<T>(Entity entity) where T : unmanaged, IComponent => inner.HasComponent<T>(entity);
        public bool TryGetComponent<T>(Entity entity, out T value) where T : unmanaged, IComponent => inner.TryGetComponent(entity, out value);
        public bool TrySetComponent<T>(Entity entity, T value) where T : unmanaged, IComponent => inner.TrySetComponent(entity, value);
        public Entity CreateEntity<TBuilder>(TBuilder builder) where TBuilder : unmanaged, IComponentsBuilder => inner.CreateEntity(builder);
        public Entity CreateEntity(in ComponentMask mask) => inner.CreateEntity(in mask);
        public Entity OverwriteEntity<TBuilder>(Entity entity, TBuilder builder) where TBuilder : unmanaged, IComponentsBuilder => inner.OverwriteEntity(entity, builder);
        public Entity AddComponents<TBuilder>(Entity entity, TBuilder builder) where TBuilder : unmanaged, IComponentsBuilder => inner.AddComponents(entity, builder);
        public void AddComponentRaw(Entity entity, ComponentId componentId, ReadOnlySpan<byte> data) => inner.AddComponentRaw(entity, componentId, data);
        public void RemoveComponentRaw(Entity entity, ComponentId componentId) => inner.RemoveComponentRaw(entity, componentId);
        public void SetComponentRaw(Entity entity, ComponentId componentId, ReadOnlySpan<byte> data) => inner.SetComponentRaw(entity, componentId, data);
        public void Clear() => inner.Clear();
        public void SetSystemRunInProgress(bool running) => inner.SetSystemRunInProgress(running);
        public void AssertStructuralChangesAllowed(string operation) => ((IWorld<ComponentMask, DefaultConfig>)inner).AssertStructuralChangesAllowed(operation);
        public void CopyFrom(ForwardingWorld source) => inner.CopyFrom(source.Inner);
        private Paradise.ECS.World<ComponentMask, DefaultConfig> Inner => inner;
    }
}
