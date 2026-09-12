using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Paradise.ECS;

/// <summary>Owns managed component slots around an ordinary or tagged ECS world.</summary>
/// <remarks>
/// Structural changes must pass through this wrapper. The exposed inner world, entity manager,
/// and chunk memory are low-level escape hatches and must not mutate managed slot storage.
/// Slot allocation is owner-thread-only. Objects are outside the bit-identical simulation contract.
/// </remarks>
public sealed partial class ManagedWorld<TMask, TConfig, TInner> : IWorld<TMask, TConfig>, IManagedWorld, ICommandExtensionSink
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new()
    where TInner : IWorld<TMask, TConfig>
{
    private readonly ImmutableArray<ManagedTypeInfo> _managedTypes;
    private readonly ManagedSlotList?[] _slots;
    private readonly Action<TInner, TInner> _copyFrom;

    public ManagedWorld(TInner inner, ImmutableArray<ManagedTypeInfo> managedTypes, Action<TInner, TInner> copyFrom)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(copyFrom);
        if (managedTypes.IsDefault)
            throw new ArgumentNullException(nameof(managedTypes));
        Inner = inner;
        _managedTypes = managedTypes;
        _copyFrom = copyFrom;
        _slots = new ManagedSlotList?[TypeInfos.Length];
        var mask = TMask.Empty;
        foreach (var info in managedTypes)
        {
            ArgumentNullException.ThrowIfNull(info);
            int id = info.SlotTypeId.Value;
            if ((uint)id >= (uint)_slots.Length || TypeInfos[id].Id != info.SlotTypeId || TypeInfos[id].Size != sizeof(int)
                || TypeInfos[id].Alignment != sizeof(int) || TypeInfos[id].ChunkAggregateSize != 0)
                throw new ArgumentException("Managed slots require a registered, four-byte aligned int component.", nameof(managedTypes));
            if (_slots[id] is not null)
                throw new ArgumentException("Managed slot component IDs must be unique.", nameof(managedTypes));
            _slots[id] = info.CreateSlots();
            mask = mask.Set(id);
        }
        ManagedSlotMask = mask;
    }

    /// <summary>The composed world; direct structural mutations bypass managed lifetime bookkeeping.</summary>
    public TInner Inner { get; }
    public TMask ManagedSlotMask { get; }
    public ImmutableArray<ComponentTypeInfo> TypeInfos => ArchetypeRegistry.TypeInfos;
    public int EntityCount => Inner.EntityCount;
    public ChunkManager ChunkManager => Inner.ChunkManager;
    public WorldEventStore Events => Inner.Events;
    public IEntityManager EntityManager => Inner.EntityManager;
    public ArchetypeRegistry<TMask, TConfig> ArchetypeRegistry => Inner.ArchetypeRegistry;
    public EntityIdAllocator EntityIdAllocator => Inner.EntityIdAllocator;

    /// <summary>Finds an extension on this wrapper before consulting the inner world.</summary>
    public TExtension? GetExtension<TExtension>() where TExtension : class
        => this as TExtension ?? Inner.GetExtension<TExtension>();

    public void SetSystemRunInProgress(bool running) => Inner.SetSystemRunInProgress(running);
    public void AssertStructuralChangesAllowed(string operation) => Inner.AssertStructuralChangesAllowed(operation);
    public bool IsAlive(Entity entity) => Inner.IsAlive(entity);
    public Entity Spawn() => Inner.Spawn();

    public EntityLocation GetLocation(Entity entity)
    {
        if (entity.IsPlaceholder || !Inner.IsAlive(entity))
            throw new InvalidOperationException($"Entity {entity} is not alive in this world.");
        return EntityManager.GetLocation(entity.Id);
    }

    public Entity GetEntity(int entityId)
    {
        var location = EntityManager.GetLocation(entityId);
        return new Entity(entityId, location.Version);
    }

    public void AddManaged<T>(Entity entity, T? value) where T : class, IManagedComponent
    {
        GetSlots<T>();
        AddManagedObject(entity, T.SlotTypeId, value);
    }

    public T? GetManaged<T>(Entity entity) where T : class, IManagedComponent
        => GetSlots<T>().Get(Handle(entity, T.SlotTypeId));

    public void SetManaged<T>(Entity entity, T? value) where T : class, IManagedComponent
    {
        GetSlots<T>();
        SetManagedObject(entity, T.SlotTypeId, value);
    }

    /// <summary>Returns presence independently of whether the stored object is null.</summary>
    public bool TryGetManaged<T>(Entity entity, out T? value) where T : class, IManagedComponent
    {
        value = null;
        if (!HasManaged<T>(entity))
            return false;
        value = GetManaged<T>(entity);
        return true;
    }

    /// <summary>Tests slot presence; null values and Skip snapshots still count as present.</summary>
    public bool HasManaged<T>(Entity entity) where T : class, IManagedComponent
    {
        GetSlots<T>();
        return HasSlot(entity, T.SlotTypeId);
    }

    public void RemoveManaged<T>(Entity entity) where T : class, IManagedComponent
    {
        GetSlots<T>();
        RemoveComponentRaw(entity, T.SlotTypeId);
    }

    private ManagedSlotList<T> GetSlots<T>() where T : class, IManagedComponent
    {
        if ((uint)T.SlotTypeId.Value >= (uint)_slots.Length || _slots[T.SlotTypeId.Value] is not ManagedSlotList<T> slots)
            throw new InvalidOperationException($"Managed component {typeof(T)} is not registered in this world.");
        return slots;
    }

    private ManagedSlotList Slots(ComponentId id)
        => IsManagedSlot(id) ? _slots[id.Value]! : throw new InvalidOperationException($"Component {id} is not a managed slot.");

    private bool IsManagedSlot(ComponentId id) => (uint)id.Value < (uint)_slots.Length && _slots[id.Value] is not null;

    private bool HasSlot(Entity entity, ComponentId id)
        => !entity.IsPlaceholder && Inner.IsAlive(entity) && ArchetypeRegistry.GetById(EntityManager.GetLocation(entity.Id).ArchetypeId)!.Layout.HasComponent(id);

    private ref int Handle(Entity entity, ComponentId id)
    {
        var location = GetLocation(entity);
        var archetype = ArchetypeRegistry.GetById(location.ArchetypeId)!;
        if (!archetype.Layout.HasComponent(id))
            throw new InvalidOperationException($"Entity {entity} does not have managed component slot {id}.");
        return ref Handle(archetype, location.GlobalIndex, id);
    }

    private ref int Handle(Archetype<TMask, TConfig> archetype, int globalIndex, ComponentId id)
    {
        var (chunkIndex, index) = archetype.GetChunkLocation(globalIndex);
        return ref ChunkManager.GetBytes(archetype.GetChunk(chunkIndex)).GetRef<int>(archetype.Layout.GetBaseOffset(id) + index * sizeof(int));
    }

    private void AddManagedObject(Entity entity, ComponentId id, object? value)
    {
        var slots = Slots(id);
        slots.ValidateObject(value);
        Span<byte> zero = stackalloc byte[sizeof(int)];
        zero.Clear();
        AddComponentRaw(entity, id, zero);
        Handle(entity, id) = slots.AllocateObject(value);
    }

    private void SetManagedObject(Entity entity, ComponentId id, object? value)
    {
        var slots = Slots(id);
        slots.ValidateObject(value);
        ref int handle = ref Handle(entity, id);
        if (value is null)
        {
            slots.Free(handle);
            handle = 0;
        }
        else if (handle == 0)
            handle = slots.AllocateObject(value);
        else
            slots.SetObject(handle, value);
    }

    public bool Despawn(Entity entity)
    {
        AssertStructuralChangesAllowed(nameof(Despawn));
        if (!IsAlive(entity))
            return Inner.Despawn(entity);
        var location = GetLocation(entity);
        var source = ArchetypeRegistry.GetById(location.ArchetypeId)!;
        Span<int> handles = stackalloc int[_managedTypes.Length];
        CaptureHandles(source, location.GlobalIndex, handles);
        if (!Inner.Despawn(entity))
            return false;
        ReleaseHandles(handles);
        ZeroTail(source);
        return true;
    }

    public Entity CreateEntity(in TMask mask)
    {
        var entity = Inner.CreateEntity(in mask);
        ZeroHandles(entity);
        return entity;
    }

    public void AddComponent<T>(Entity entity, T value = default) where T : unmanaged, IComponent
    {
        if (IsManagedSlot(T.TypeId))
        {
            AddComponentRaw(entity, T.TypeId, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in value, 1)));
            return;
        }
        var source = ArchetypeRegistry.GetById(GetLocation(entity).ArchetypeId)!;
        Inner.AddComponent(entity, value);
        ZeroTail(source);
    }

    public void RemoveComponent<T>(Entity entity) where T : unmanaged, IComponent => RemoveComponentRaw(entity, T.TypeId);

    public ref T GetComponent<T>(Entity entity) where T : unmanaged, IComponent
    {
        if (IsManagedSlot(T.TypeId))
            throw new InvalidOperationException("Managed slots cannot be accessed by writable component reference; use GetManaged/SetManaged.");
        return ref Inner.GetComponent<T>(entity);
    }

    public bool HasComponent<T>(Entity entity) where T : unmanaged, IComponent => Inner.HasComponent<T>(entity);
    public bool TryGetComponent<T>(Entity entity, out T value) where T : unmanaged, IComponent => Inner.TryGetComponent(entity, out value);

    public bool TrySetComponent<T>(Entity entity, T value) where T : unmanaged, IComponent
    {
        if (!IsManagedSlot(T.TypeId))
            return Inner.TrySetComponent(entity, value);
        if (!Inner.HasComponent<T>(entity))
            return false;
        SetComponentRaw(entity, T.TypeId, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in value, 1)));
        return true;
    }

    public void AddComponentRaw(Entity entity, ComponentId componentId, ReadOnlySpan<byte> data)
    {
        ValidateRawSlot(componentId, data);
        var source = ArchetypeRegistry.GetById(GetLocation(entity).ArchetypeId)!;
        Inner.AddComponentRaw(entity, componentId, data);
        ZeroTail(source);
    }

    public void RemoveComponentRaw(Entity entity, ComponentId componentId)
    {
        AssertStructuralChangesAllowed(nameof(RemoveComponentRaw));
        var source = ArchetypeRegistry.GetById(GetLocation(entity).ArchetypeId)!;
        int handle = IsManagedSlot(componentId) ? Handle(entity, componentId) : 0;
        Inner.RemoveComponentRaw(entity, componentId);
        if (handle != 0)
            Slots(componentId).Free(handle);
        ZeroTail(source);
    }

    public void SetComponentRaw(Entity entity, ComponentId componentId, ReadOnlySpan<byte> data)
    {
        ValidateRawSlot(componentId, data);
        if (IsManagedSlot(componentId))
            SetManagedObject(entity, componentId, null);
        else
            Inner.SetComponentRaw(entity, componentId, data);
    }

    private void ValidateRawSlot(ComponentId id, ReadOnlySpan<byte> data)
    {
        if (!IsManagedSlot(id))
            return;
        if (data.Length != sizeof(int))
            throw new ArgumentException("Managed slot payloads must contain exactly one int.", nameof(data));
        if (MemoryMarshal.Read<int>(data) != 0)
            throw new InvalidOperationException("Raw component data cannot forge managed handles; use AddManaged/SetManaged.");
    }

    private void CaptureHandles(Archetype<TMask, TConfig> archetype, int globalIndex, Span<int> handles)
    {
        for (int i = 0; i < _managedTypes.Length; i++)
        {
            var id = _managedTypes[i].SlotTypeId;
            handles[i] = archetype.Layout.HasComponent(id) ? Handle(archetype, globalIndex, id) : 0;
        }
    }

    private void ReleaseHandles(ReadOnlySpan<int> handles)
    {
        for (int i = 0; i < _managedTypes.Length; i++)
            Slots(_managedTypes[i].SlotTypeId).Free(handles[i]);
    }

    private void ZeroHandles(Entity entity)
    {
        var location = GetLocation(entity);
        var archetype = ArchetypeRegistry.GetById(location.ArchetypeId)!;
        foreach (var info in _managedTypes)
        {
            if (archetype.Layout.HasComponent(info.SlotTypeId))
                Handle(archetype, location.GlobalIndex, info.SlotTypeId) = 0;
        }
    }

    private void ZeroTail(Archetype<TMask, TConfig> archetype)
    {
        // Swap-removal leaves the victim's old bytes behind; clear only a tail that still owns a chunk.
        if (archetype.EntityCount / archetype.Layout.EntitiesPerChunk >= archetype.ChunkCount)
            return;
        foreach (var info in _managedTypes)
        {
            if (archetype.Layout.HasComponent(info.SlotTypeId))
                Handle(archetype, archetype.EntityCount, info.SlotTypeId) = 0;
        }
    }

    public void Clear()
    {
        Inner.Clear();
        foreach (var info in _managedTypes)
            Slots(info.SlotTypeId).Clear();
    }

    /// <summary>Copies chunk handles and typed object stores according to each component's snapshot policy.</summary>
    /// <remarks>Reference snapshots observe shared objects at read time; a failed clone clears the destination.</remarks>
    public void CopyFrom(ManagedWorld<TMask, TConfig, TInner> source)
    {
        AssertStructuralChangesAllowed(nameof(CopyFrom));
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(this, source))
            throw new InvalidOperationException("Cannot copy a world to itself.");
        if (!ReferenceEquals(ArchetypeRegistry.SharedMetadata, source.ArchetypeRegistry.SharedMetadata))
            throw new InvalidOperationException("Worlds must share the same SharedArchetypeMetadata.");
        if (_managedTypes.Length != source._managedTypes.Length)
            throw new InvalidOperationException("Worlds must use the same managed component registry.");
        for (int i = 0; i < _managedTypes.Length; i++)
        {
            var info = _managedTypes[i];
            var other = source._managedTypes[i];
            if (info.Type != other.Type || info.SlotTypeId != other.SlotTypeId || info.Guid != other.Guid || info.Snapshot != other.Snapshot)
                throw new InvalidOperationException("Worlds must use the same managed component registry.");
        }
        try
        {
            _copyFrom(Inner, source.Inner);
            foreach (var info in _managedTypes)
                Slots(info.SlotTypeId).CopyFrom(source.Slots(info.SlotTypeId));
        }
        catch
        {
            Clear();
            throw;
        }
    }

    public void PlayExtension(Type opType, Entity entity, ReadOnlySpan<byte> data)
    {
        if (opType == typeof(AddManagedOp) || opType == typeof(SetManagedOp) || opType == typeof(RemoveManagedOp))
            throw new InvalidOperationException("Managed commands require their originating command buffer for playback.");
        if (Inner is ICommandExtensionSink sink)
            sink.PlayExtension(opType, entity, data);
        else
            throw new NotSupportedException($"Unknown command extension {opType}.");
    }

    public void PlayExtension(EntityCommandBuffer buffer, Type opType, Entity entity, ReadOnlySpan<byte> data)
    {
        if (opType != typeof(AddManagedOp) && opType != typeof(SetManagedOp) && opType != typeof(RemoveManagedOp))
        {
            if (Inner is ICommandExtensionSink sink)
                sink.PlayExtension(buffer, opType, entity, data);
            else
                throw new NotSupportedException($"Unknown command extension {opType}.");
            return;
        }
        if (data.Length != sizeof(int) * 2)
            throw new ArgumentException("Invalid managed command payload.", nameof(data));
        var id = new ComponentId(MemoryMarshal.Read<int>(data));
        int index = MemoryMarshal.Read<int>(data[sizeof(int)..]);
        if (!buffer.TryGetExtensionState<ManagedCommandState>(out var state))
            throw new InvalidOperationException("The command buffer has no managed staging state.");
        object? value = state.Get(index, id, _managedTypes);
        if (opType == typeof(RemoveManagedOp))
            RemoveComponentRaw(entity, id);
        else if (opType == typeof(AddManagedOp))
            AddManagedObject(entity, id, value);
        else
            SetManagedObject(entity, id, value);
    }
}
