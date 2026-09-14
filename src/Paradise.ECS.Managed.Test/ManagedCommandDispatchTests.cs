using System.Runtime.InteropServices;

namespace Paradise.ECS.Managed.Test;

public sealed class ManagedCommandDispatchTests
{
    [Test]
    public async Task ManagedWorldForwardsOriginatingBufferStateAndResolvedEntityToCustomSink()
    {
        using var shared = new SharedManagedWorld<ComponentMask, DefaultConfig, ForwardingSinkWorld>(
            ComponentRegistry.Shared.TypeInfos, ManagedRegistry.TypeInfos,
            static (config, chunks, metadata) => new ForwardingSinkWorld(new Paradise.ECS.World<ComponentMask, DefaultConfig>(config, metadata, chunks)),
            static (target, source) => target.CopyFrom(source));
        var world = shared.CreateWorld();
        using var first = new EntityCommandBuffer();
        using var second = new EntityCommandBuffer();
        var firstValue = new object();
        var secondValue = new object();
        first.GetOrCreateExtensionState<ForwardedState>().Value = firstValue;
        second.GetOrCreateExtensionState<ForwardedState>().Value = secondValue;
        var pending = first.Spawn();
        first.AddManaged(pending, new RuntimePayload { Value = 12 });
        first.RecordExtension<ForwardedOp>(pending, BitConverter.GetBytes(73));

        first.Playback(world);

        var entity = first.Resolve(pending);
        await Assert.That(ReferenceEquals(world.Inner.LastBuffer, first)).IsTrue();
        await Assert.That(world.Inner.LastEntity).IsEqualTo(entity);
        await Assert.That(ReferenceEquals(world.Inner.LastValue, firstValue)).IsTrue();
        await Assert.That(world.Inner.LastPayload).IsEqualTo(73);
        await Assert.That(world.GetManaged<RuntimePayload>(entity)!.Value).IsEqualTo(12);

        second.RecordExtension<ForwardedOp>(entity, BitConverter.GetBytes(19));
        second.SetManaged(entity, new RuntimePayload { Value = 25 });
        second.Playback(world);

        await Assert.That(ReferenceEquals(world.Inner.LastBuffer, second)).IsTrue();
        await Assert.That(world.Inner.LastEntity).IsEqualTo(entity);
        await Assert.That(ReferenceEquals(world.Inner.LastValue, secondValue)).IsTrue();
        await Assert.That(world.Inner.LastPayload).IsEqualTo(19);
        await Assert.That(world.GetManaged<RuntimePayload>(entity)!.Value).IsEqualTo(25);
        first.Clear();
        await Assert.That(first.GetOrCreateExtensionState<ForwardedState>().Value).IsNull();
        await Assert.That(ReferenceEquals(second.GetOrCreateExtensionState<ForwardedState>().Value, secondValue)).IsTrue();
    }

    public sealed class ForwardedState : ICommandBufferExtensionState
    {
        public object? Value { get; set; }
        public void Clear() => Value = null;
    }

    private readonly struct ForwardedOp : ICommandExtension;

    private sealed class ForwardingSinkWorld(Paradise.ECS.World<ComponentMask, DefaultConfig> inner)
        : IWorld<ComponentMask, DefaultConfig>, ICommandExtensionSink
    {
        public EntityCommandBuffer? LastBuffer { get; private set; }
        public Entity LastEntity { get; private set; }
        public object? LastValue { get; private set; }
        public int LastPayload { get; private set; }

        public void PlayExtension(EntityCommandBuffer buffer, Type opType, Entity entity, ReadOnlySpan<byte> data)
        {
            if (opType != typeof(ForwardedOp) || !buffer.TryGetExtensionState<ForwardedState>(out var state))
                throw new InvalidOperationException("The forwarded command has no matching staging state.");
            LastBuffer = buffer;
            LastEntity = entity;
            LastValue = state.Value;
            LastPayload = MemoryMarshal.Read<int>(data);
        }

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
        public void CopyFrom(ForwardingSinkWorld source) => inner.CopyFrom(source.Inner);
        private Paradise.ECS.World<ComponentMask, DefaultConfig> Inner => inner;
    }
}
