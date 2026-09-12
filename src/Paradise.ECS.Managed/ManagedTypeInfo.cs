namespace Paradise.ECS;

/// <summary>Describes a managed component and constructs its typed storage without reflection.</summary>
public sealed class ManagedTypeInfo
{
    private readonly Func<ManagedSlotList> _createSlots;

    private ManagedTypeInfo(Type type, ComponentId slotTypeId, Guid guid, ManagedSnapshot snapshot,
        Func<ManagedSlotList> createSlots)
    {
        Type = type;
        SlotTypeId = slotTypeId;
        Guid = guid;
        Snapshot = snapshot;
        _createSlots = createSlots;
    }

    public Type Type { get; }
    public ComponentId SlotTypeId { get; }
    public Guid Guid { get; }
    public ManagedSnapshot Snapshot { get; }

    /// <summary>Creates metadata for a concrete component and optional clone delegate.</summary>
    public static ManagedTypeInfo Create<T>(ManagedSnapshot snapshot = ManagedSnapshot.Reference, Func<T, T>? clone = null)
        where T : class, IManagedComponent
    {
        if (snapshot is < ManagedSnapshot.Reference or > ManagedSnapshot.Skip)
            throw new ArgumentOutOfRangeException(nameof(snapshot));
        if (snapshot == ManagedSnapshot.Clone && clone is null)
            throw new ArgumentException("Clone snapshots require a clone delegate.", nameof(clone));
        if (!T.SlotTypeId.IsValid)
            throw new ArgumentException("Managed component slot IDs must be nonnegative.", nameof(T));
        return new ManagedTypeInfo(typeof(T), T.SlotTypeId, T.Guid, snapshot,
            () => new ManagedSlotList<T>(snapshot, clone));
    }

    /// <summary>Creates clone metadata using static interface dispatch, including explicit implementations.</summary>
    public static ManagedTypeInfo CreateClone<T>() where T : class, IManagedComponent, IManagedClone<T>
        => Create<T>(ManagedSnapshot.Clone, static source => T.Clone(source));

    internal ManagedSlotList CreateSlots() => _createSlots();
}

internal abstract class ManagedSlotList
{
    public abstract int AllocateObject(object? value);
    public abstract void SetObject(int handle, object? value);
    public abstract void ValidateObject(object? value);
    public abstract void Free(int handle);
    public abstract void Clear();
    public abstract void CopyFrom(ManagedSlotList source);
}

internal sealed class ManagedSlotList<T>(ManagedSnapshot snapshot, Func<T, T>? clone) : ManagedSlotList
    where T : class, IManagedComponent
{
    private T?[] _slots = new T?[8];
    private bool[] _occupied = new bool[8];
    private int[] _free = new int[8];
    private int _next = 1;
    private int _freeCount;

    public T? Get(int handle)
    {
        if (handle == 0)
            return null;
        ValidateHandle(handle);
        return _slots[handle];
    }

    public int Allocate(T? value)
    {
        if (value is null)
            return 0;
        int handle = _freeCount == 0 ? _next++ : _free[--_freeCount];
        EnsureCapacity(_next);
        _occupied[handle] = true;
        _slots[handle] = value;
        return handle;
    }

    public void Set(int handle, T? value)
    {
        ValidateHandle(handle);
        _slots[handle] = value;
    }

    public override void ValidateObject(object? value)
    {
        if (value is not null and not T)
            throw new ArgumentException($"Expected a managed component of type {typeof(T)}.", nameof(value));
    }

    public override int AllocateObject(object? value)
    {
        ValidateObject(value);
        return Allocate((T?)value);
    }

    public override void SetObject(int handle, object? value)
    {
        ValidateObject(value);
        Set(handle, (T?)value);
    }

    public override void Free(int handle)
    {
        if (handle == 0)
            return;
        ValidateHandle(handle);
        _slots[handle] = null;
        _occupied[handle] = false;
        _free[_freeCount++] = handle;
    }

    public override void Clear()
    {
        Array.Clear(_slots);
        Array.Clear(_occupied);
        Array.Clear(_free);
        _next = 1;
        _freeCount = 0;
    }

    public override void CopyFrom(ManagedSlotList source)
    {
        var typed = (ManagedSlotList<T>)source;
        Clear();
        EnsureCapacity(typed._next);
        _next = typed._next;
        _freeCount = typed._freeCount;
        typed._occupied.AsSpan(0, _next).CopyTo(_occupied);
        typed._free.AsSpan(0, _freeCount).CopyTo(_free);
        if (snapshot == ManagedSnapshot.Reference)
            typed._slots.AsSpan(0, _next).CopyTo(_slots);
        else if (snapshot == ManagedSnapshot.Clone)
        {
            for (int i = 1; i < _next; i++)
            {
                if (typed._slots[i] is { } value)
                    _slots[i] = clone!(value) ?? throw new InvalidOperationException("A managed clone returned null.");
            }
        }
    }

    private void ValidateHandle(int handle)
    {
        if (handle <= 0 || handle >= _next || !_occupied[handle])
            throw new InvalidOperationException("The managed slot handle is stale or was modified outside ManagedWorld.");
    }

    private void EnsureCapacity(int count)
    {
        if (count <= _slots.Length)
            return;
        int capacity = Math.Max(count, _slots.Length * 2);
        Array.Resize(ref _slots, capacity);
        Array.Resize(ref _occupied, capacity);
        Array.Resize(ref _free, capacity);
    }
}
