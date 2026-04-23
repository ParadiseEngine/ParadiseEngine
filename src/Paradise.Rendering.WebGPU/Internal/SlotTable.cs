using System;
using System.Collections.Generic;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Generation-tracked slot allocator mapping <c>(uint Index, uint Generation)</c>
/// handles to backend objects of type <typeparamref name="T"/>. Generation 0 is reserved as the
/// invalid sentinel — every issued handle has <c>Generation &gt;= 1</c>, matching the
/// <c>IsValid</c> contract on <c>Paradise.Rendering</c>'s handle structs.</summary>
internal sealed class SlotTable<T> where T : class
{
    private readonly List<Slot> _slots = new();
    private readonly Stack<uint> _free = new();

    private struct Slot
    {
        public T? Value;
        public uint Generation;
    }

    public int Count => _slots.Count - _free.Count;

    public (uint Index, uint Generation) Add(T value)
    {
        if (_free.TryPop(out var index))
        {
            ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_slots)[(int)index];
            slot.Value = value;
            return (index, slot.Generation);
        }

        index = (uint)_slots.Count;
        _slots.Add(new Slot { Value = value, Generation = 1 });
        return (index, 1u);
    }

    public bool TryGet(uint index, uint generation, out T value)
    {
        if (index >= (uint)_slots.Count)
        {
            value = null!;
            return false;
        }

        var slot = _slots[(int)index];
        if (slot.Generation != generation || slot.Value is null)
        {
            value = null!;
            return false;
        }

        value = slot.Value;
        return true;
    }

    public bool Remove(uint index, uint generation)
    {
        if (index >= (uint)_slots.Count) return false;
        ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_slots)[(int)index];
        if (slot.Generation != generation || slot.Value is null) return false;

        slot.Value = null;
        // Bump generation so a future allocation in this slot produces a distinct handle and stale
        // handles to the removed entry stop resolving. Skip generation 0 on wraparound — that
        // value is the invalid sentinel for handle structs.
        unchecked { slot.Generation++; }
        if (slot.Generation == 0) slot.Generation = 1;

        _free.Push(index);
        return true;
    }

    public void Clear()
    {
        _slots.Clear();
        _free.Clear();
    }
}
