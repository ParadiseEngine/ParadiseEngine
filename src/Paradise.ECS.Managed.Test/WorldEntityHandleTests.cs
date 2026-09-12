using System.Runtime.CompilerServices;

namespace Paradise.ECS.Managed.Test;

[Component]
public partial struct HandleMarker;

public sealed class WorldEntityHandleTests
{
    [Test]
    public async Task UnifiedAccessDispatchesStructTagAndManagedOperationsThroughOuterWorld()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var entity = world.Spawn();
        var handle = new WorldEntity(world, entity);
        var payload = new RuntimePayload { Value = 31 };

        await Assert.That(ReferenceEquals(handle.World, world)).IsTrue();
        await Assert.That(handle.Entity).IsEqualTo(entity);
        await Assert.That(handle.IsAlive).IsTrue();
        handle.Add(new RuntimeNumber { Value = 7 });
        handle.Add<RuntimeTag>();
        handle.Add(payload);

        await Assert.That(handle.Has<RuntimeNumber>()).IsTrue();
        await Assert.That(handle.Has<RuntimeTag>()).IsTrue();
        await Assert.That(handle.Has<RuntimePayload>()).IsTrue();
        await Assert.That(handle.Get<RuntimeNumber>().Value).IsEqualTo(7);
        await Assert.That(ReferenceEquals(payload, handle.Get<RuntimePayload>())).IsTrue();
        await Assert.That(handle.TryGet<RuntimeNumber>(out var number)).IsTrue();
        await Assert.That(number.Value).IsEqualTo(7);
        await Assert.That(handle.TryGet<RuntimePayload>(out var managed)).IsTrue();
        await Assert.That(ReferenceEquals(payload, managed)).IsTrue();

        var replacement = new RuntimePayload { Value = 41 };
        handle.Set(new RuntimeNumber { Value = 11 });
        handle.Set(replacement);
        await Assert.That(world.GetComponent<RuntimeNumber>(entity).Value).IsEqualTo(11);
        await Assert.That(ReferenceEquals(replacement, world.GetManaged<RuntimePayload>(entity))).IsTrue();

        handle.Remove<RuntimeNumber>();
        handle.Remove<RuntimeTag>();
        handle.Remove<RuntimePayload>();
        await Assert.That(handle.Has<RuntimeNumber>()).IsFalse();
        await Assert.That(handle.Has<RuntimeTag>()).IsFalse();
        await Assert.That(handle.Has<RuntimePayload>()).IsFalse();
        await Assert.That(handle.TryGet<RuntimeNumber>(out _)).IsFalse();
        await Assert.That(handle.TryGet<RuntimePayload>(out _)).IsFalse();
        await Assert.That(handle.IsAlive).IsTrue();
    }

    [Test]
    public async Task NoValueAddUsesStructDefaultsAndPresentNullManagedSlots()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var handle = new WorldEntity(world, world.Spawn());
        handle.Add<RuntimeNumber>();
        handle.Add<RuntimePayload>();
        handle.Add<RuntimeTag>();

        await Assert.That(handle.Get<RuntimeNumber>().Value).IsEqualTo(0);
        await Assert.That(handle.Has<RuntimeTag>()).IsTrue();
        await Assert.That(handle.Has<RuntimePayload>()).IsTrue();
        await Assert.That(handle.Get<RuntimePayload>()).IsNull();
        await Assert.That(handle.TryGet<RuntimePayload>(out var value)).IsTrue();
        await Assert.That(value).IsNull();

        handle.Set(new RuntimePayload());
        handle.Set<RuntimePayload>(null);
        await Assert.That(handle.Has<RuntimePayload>()).IsTrue();
        await Assert.That(handle.Get<RuntimePayload>()).IsNull();
    }

    [Test]
    public async Task StoredHandleResolvesCurrentLocationAfterArchetypeMovesAndSwapRemoval()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var first = new WorldEntity(world, world.Spawn());
        var stored = new List<WorldEntity> { new(world, world.Spawn()) };
        first.Add(new RuntimeNumber { Value = 1 });
        stored[0].Add(new RuntimeNumber { Value = 2 });
        first.Add(new RuntimePayload { Value = 10 });
        stored[0].Add(new RuntimePayload { Value = 20 });

        await Assert.That(first.Despawn()).IsTrue();
        await Assert.That(stored[0].Get<RuntimeNumber>().Value).IsEqualTo(2);
        stored[0].GetRef<RuntimeNumber>().Value = 3;
        stored[0].Add(new SkipPayload { Value = 99 });
        await Assert.That(stored[0].Get<RuntimeNumber>().Value).IsEqualTo(3);
        stored[0].Remove<SkipPayload>();
        stored[0].GetRef<RuntimeNumber>().Value = 4;
        await Assert.That(world.GetComponent<RuntimeNumber>(stored[0].Entity).Value).IsEqualTo(4);
        await Assert.That(stored[0].Get<RuntimePayload>()!.Value).IsEqualTo(20);
    }

    [Test]
    public async Task StaleGenerationNeverRetargetsARecycledEntityId()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var stale = new WorldEntity(world, world.Spawn());
        stale.Add(new RuntimeNumber { Value = 1 });
        stale.Add(new RuntimePayload { Value = 2 });
        stale.Add<RuntimeTag>();
        await Assert.That(stale.Despawn()).IsTrue();

        var replacement = new WorldEntity(world, world.Spawn());
        replacement.Add(new RuntimeNumber { Value = 11 });
        replacement.Add(new RuntimePayload { Value = 22 });
        replacement.Add<RuntimeTag>();
        await Assert.That(replacement.Entity.Id).IsEqualTo(stale.Entity.Id);
        await Assert.That(replacement.Entity.Version).IsNotEqualTo(stale.Entity.Version);
        await Assert.That(stale.IsAlive).IsFalse();
        await Assert.That(stale.Has<RuntimeNumber>()).IsFalse();
        await Assert.That(stale.Has<RuntimeTag>()).IsFalse();
        await Assert.That(stale.Has<RuntimePayload>()).IsFalse();
        await Assert.That(stale.TryGet<RuntimeNumber>(out _)).IsFalse();
        await Assert.That(stale.TryGet<RuntimePayload>(out _)).IsFalse();
        await Assert.That(stale.Despawn()).IsFalse();
        await Assert.That(stale.Get<RuntimeNumber>).Throws<InvalidOperationException>();
        await Assert.That(stale.Get<RuntimePayload>).Throws<InvalidOperationException>();
        await Assert.That(() => stale.Set(new RuntimeNumber())).Throws<InvalidOperationException>();
        await Assert.That(() => stale.Set(new RuntimePayload())).Throws<InvalidOperationException>();
        await Assert.That(stale.Add<RuntimeTag>).Throws<InvalidOperationException>();
        await Assert.That(stale.Remove<RuntimeNumber>).Throws<InvalidOperationException>();
        await Assert.That(stale.Remove<RuntimeTag>).Throws<InvalidOperationException>();
        await Assert.That(stale.Remove<RuntimePayload>).Throws<InvalidOperationException>();
        await Assert.That(() => ReadRefValue(stale)).Throws<InvalidOperationException>();
        await Assert.That(replacement.Get<RuntimeNumber>().Value).IsEqualTo(11);
        await Assert.That(replacement.Get<RuntimePayload>()!.Value).IsEqualTo(22);
        await Assert.That(replacement.Has<RuntimeTag>()).IsTrue();
    }

    [Test]
    public async Task RemoveAndDespawnReleaseManagedReferencesThroughOuterWrapper()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var removed = new WorldEntity(world, world.Spawn());
        var despawned = new WorldEntity(world, world.Spawn());
        var removedReference = AddWeakPayload(removed);
        var despawnedReference = AddWeakPayload(despawned);
        removed.Remove<RuntimePayload>();
        await Assert.That(despawned.Despawn()).IsTrue();
        Collect();
        await Assert.That(removedReference.IsAlive).IsFalse();
        await Assert.That(despawnedReference.IsAlive).IsFalse();

        removed.Add<RuntimePayload>();
        await Assert.That(removed.Get<RuntimePayload>()).IsNull();
        removed.Set(new RuntimePayload { Value = 9 });
        await Assert.That(removed.Get<RuntimePayload>()!.Value).IsEqualTo(9);
    }

    [Test]
    public async Task DefaultHandleRejectsOperationsAndNullWorldConstructorIsInvalid()
    {
        var handle = default(WorldEntity);
        await Assert.That(handle.IsAlive).IsFalse();
        await Assert.That(() => new WorldEntity(null!, default)).Throws<ArgumentNullException>();
        await Assert.That(handle.Despawn).Throws<InvalidOperationException>();
        await Assert.That(handle.Add<RuntimeNumber>).Throws<InvalidOperationException>();
        await Assert.That(handle.Add<RuntimeTag>).Throws<InvalidOperationException>();
        await Assert.That(handle.Add<RuntimePayload>).Throws<InvalidOperationException>();
        await Assert.That(handle.Has<RuntimeNumber>).Throws<InvalidOperationException>();
        await Assert.That(handle.Has<RuntimeTag>).Throws<InvalidOperationException>();
        await Assert.That(handle.Has<RuntimePayload>).Throws<InvalidOperationException>();
        await Assert.That(handle.Get<RuntimeNumber>).Throws<InvalidOperationException>();
        await Assert.That(handle.Get<RuntimePayload>).Throws<InvalidOperationException>();
        await Assert.That(() => handle.Set(new RuntimeNumber())).Throws<InvalidOperationException>();
        await Assert.That(() => handle.Set<RuntimePayload>(null)).Throws<InvalidOperationException>();
        await Assert.That(() => handle.TryGet<RuntimeNumber>(out _)).Throws<InvalidOperationException>();
        await Assert.That(() => handle.TryGet<RuntimePayload>(out _)).Throws<InvalidOperationException>();
        await Assert.That(handle.Remove<RuntimeNumber>).Throws<InvalidOperationException>();
        await Assert.That(handle.Remove<RuntimeTag>).Throws<InvalidOperationException>();
        await Assert.That(handle.Remove<RuntimePayload>).Throws<InvalidOperationException>();
        await Assert.That(() => ReadRefValue(handle)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task PlainWorldKeepsStructAccessAndRejectsUnsupportedTagAndManagedCapabilities()
    {
        using var shared = new Paradise.ECS.SharedWorld<ComponentMask, DefaultConfig>(ComponentRegistry.Shared.TypeInfos);
        var world = shared.CreateWorld();
        var handle = new WorldEntity(world, world.Spawn());
        handle.Add(new RuntimeNumber { Value = 8 });
        handle.Set(new RuntimeNumber { Value = 9 });
        await Assert.That(handle.Get<RuntimeNumber>().Value).IsEqualTo(9);
        await Assert.That(handle.Add<RuntimeTag>).Throws<NotSupportedException>();
        await Assert.That(handle.Has<RuntimeTag>).Throws<NotSupportedException>();
        await Assert.That(handle.Remove<RuntimeTag>).Throws<NotSupportedException>();
        await Assert.That(handle.Add<RuntimePayload>).Throws<NotSupportedException>();
        await Assert.That(handle.Has<RuntimePayload>).Throws<NotSupportedException>();
        await Assert.That(handle.Get<RuntimePayload>).Throws<NotSupportedException>();
        await Assert.That(() => handle.Set<RuntimePayload>(null)).Throws<NotSupportedException>();
        await Assert.That(handle.Remove<RuntimePayload>).Throws<NotSupportedException>();
        await Assert.That(() => handle.TryGet<RuntimePayload>(out _)).Throws<NotSupportedException>();
        await Assert.That(handle.IsAlive).IsTrue();
        await Assert.That(handle.Get<RuntimeNumber>().Value).IsEqualTo(9);
    }

    [Test]
    public async Task ExplicitLegacyGenericWorldMembersBridgeToStableHandleAndBaseInterface()
    {
        using var shared = SharedWorldFactory.Create();
        IWorld world = new ForwardingWorld(shared.CreateWorld());
        var entity = world.Spawn();
        var handle = new WorldEntity(world, entity);
        await Assert.That(world.EntityCount).IsEqualTo(1);
        await Assert.That(world.IsAlive(entity)).IsTrue();
        await Assert.That(handle.IsAlive).IsTrue();

        handle.Add<RuntimeNumber>();
        handle.Set(new RuntimeNumber { Value = 17 });
        await Assert.That(handle.Has<RuntimeNumber>()).IsTrue();
        await Assert.That(handle.Get<RuntimeNumber>().Value).IsEqualTo(17);
        handle.Remove<RuntimeNumber>();
        await Assert.That(handle.Has<RuntimeNumber>()).IsFalse();
        handle.Add(new RuntimeNumber { Value = 23 });
        await Assert.That(world.GetComponent<RuntimeNumber>(entity).Value).IsEqualTo(23);

        await Assert.That(handle.Despawn()).IsTrue();
        await Assert.That(world.EntityCount).IsEqualTo(0);
        await Assert.That(world.IsAlive(entity)).IsFalse();
        await Assert.That(handle.IsAlive).IsFalse();
        await Assert.That(handle.Despawn()).IsFalse();
    }

    [Test]
    public async Task WarmUnmanagedHandleAccessDoesNotAllocate()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var handle = new WorldEntity(world, world.Spawn());
        handle.Add<RuntimeNumber>();
        ExerciseUnmanagedAccess(handle, 20);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int total = ExerciseUnmanagedAccess(handle, 1000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(total).IsEqualTo(499500);
        await Assert.That(allocated).IsEqualTo(0L);
    }

    [Test]
    public async Task EmptyComponentValuesNeverOverwriteEntityIdsAndHaveNoRefStorage()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        world.Spawn();
        var handles = new List<WorldEntity>();
        for (int i = 0; i < 3; i++)
        {
            var handle = new WorldEntity(world, world.Spawn());
            handle.Add(new RuntimeNumber { Value = i + 10 });
            handle.Add<HandleMarker>();
            handles.Add(handle);
        }

        await Assert.That(HandleMarker.Size).IsEqualTo(0);
        foreach (var handle in handles)
        {
            handle.Set(default(HandleMarker));
            _ = handle.Get<HandleMarker>();
            await Assert.That(handle.TryGet<HandleMarker>(out _)).IsTrue();
            await Assert.That(handle.Has<HandleMarker>()).IsTrue();
            await Assert.That(() => ReadMarkerRef(handle)).Throws<InvalidOperationException>();
        }

        var queried = new List<Entity>();
        foreach (var item in QueryBuilder<ComponentMask>.Create().With<HandleMarker>().Build(world))
            queried.Add(item.Entity);
        await Assert.That(queried.Count).IsEqualTo(handles.Count);
        foreach (var handle in handles)
        {
            await Assert.That(queried.Contains(handle.Entity)).IsTrue();
            await Assert.That(world.GetComponent<RuntimeNumber>(handle.Entity).Value).IsEqualTo(handle.Entity.Id + 9);
        }

        var removed = handles[0];
        removed.Remove<HandleMarker>();
        await Assert.That(removed.Get<HandleMarker>).Throws<InvalidOperationException>();
        await Assert.That(() => removed.Set(default(HandleMarker))).Throws<InvalidOperationException>();
        await Assert.That(removed.TryGet<HandleMarker>(out _)).IsFalse();
    }

#if DEBUG
    [Test]
    public async Task StructuralGuardAppliesToStoredHandleBeforeManagedLifetimeChanges()
    {
        using var shared = SharedWorldFactory.Create();
        var world = shared.CreateWorld();
        var handle = new WorldEntity(world, world.Spawn());
        var payload = new RuntimePayload { Value = 5 };
        handle.Add(payload);
        handle.Add<RuntimeTag>();
        world.SetSystemRunInProgress(true);
        try
        {
            await Assert.That(handle.Add<RuntimeNumber>).Throws<InvalidOperationException>();
            await Assert.That(handle.Remove<RuntimePayload>).Throws<InvalidOperationException>();
            await Assert.That(handle.Remove<RuntimeTag>).Throws<InvalidOperationException>();
            await Assert.That(handle.Despawn).Throws<InvalidOperationException>();
            await Assert.That(handle.IsAlive).IsTrue();
            await Assert.That(ReferenceEquals(payload, handle.Get<RuntimePayload>())).IsTrue();
        }
        finally
        {
            world.SetSystemRunInProgress(false);
        }
    }
#endif

    private static int ReadRefValue(WorldEntity handle) => handle.GetRef<RuntimeNumber>().Value;

    private static void ReadMarkerRef(WorldEntity handle) => _ = handle.GetRef<HandleMarker>();

    private static int ExerciseUnmanagedAccess(WorldEntity handle, int count)
    {
        int total = 0;
        for (int i = 0; i < count; i++)
        {
            handle.Set(new RuntimeNumber { Value = i });
            if (!handle.Has<RuntimeNumber>())
                throw new InvalidOperationException("The component disappeared during handle access.");
            total += handle.Get<RuntimeNumber>().Value;
        }
        return total;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AddWeakPayload(WorldEntity handle)
    {
        var payload = new RuntimePayload();
        handle.Add(payload);
        return new WeakReference(payload);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class ForwardingWorld(World inner) : IWorld<ComponentMask, DefaultConfig>
    {
        Entity IWorld<ComponentMask, DefaultConfig>.Spawn() => inner.Spawn();
        bool IWorld<ComponentMask, DefaultConfig>.Despawn(Entity entity) => inner.Despawn(entity);
        bool IWorld<ComponentMask, DefaultConfig>.IsAlive(Entity entity) => inner.IsAlive(entity);
        int IWorld<ComponentMask, DefaultConfig>.EntityCount => inner.EntityCount;
        void IWorld<ComponentMask, DefaultConfig>.AddComponent<T>(Entity entity, T value) => inner.AddComponent(entity, value);
        void IWorld<ComponentMask, DefaultConfig>.RemoveComponent<T>(Entity entity) => inner.RemoveComponent<T>(entity);

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
        public void AssertStructuralChangesAllowed(string operation) => inner.AssertStructuralChangesAllowed(operation);
    }
}
