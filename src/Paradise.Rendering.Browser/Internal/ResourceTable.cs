using System;
using System.Collections.Generic;

namespace Paradise.Rendering.Browser.Internal;

/// <summary>Tracks resource generations at matching managed and JavaScript slot indices.</summary>
/// <remarks>Generation zero is invalid. Freed slots are reused with a new generation so stale
/// handles cannot address replacement objects.</remarks>
internal sealed class ResourceTable
{
    private readonly List<uint> _generations = new();
    private readonly List<bool> _alive = new();
    private readonly Stack<uint> _free = new();

    /// <summary>Slots currently holding a live resource — the leak metric.</summary>
    public int LiveCount => _generations.Count - _free.Count;

    /// <summary>Reserve a slot and return its index; <paramref name="generation"/> receives the
    /// generation the caller must stamp into the public handle.</summary>
    public uint Allocate(out uint generation)
    {
        if (_free.TryPop(out var index))
        {
            _alive[(int)index] = true;
            generation = _generations[(int)index];
            return index;
        }

        index = (uint)_generations.Count;
        _generations.Add(1u);
        _alive.Add(true);
        generation = 1u;
        return index;
    }

    public bool IsAlive(uint index, uint generation) =>
        index < (uint)_generations.Count && _alive[(int)index] && _generations[(int)index] == generation;

    /// <summary>Validate a handle and return its JS table index, throwing
    /// <see cref="StaleHandleException"/> when the handle is stale or was never issued.</summary>
    public uint Resolve(uint index, uint generation, string kind)
    {
        if (!IsAlive(index, generation))
            throw new StaleHandleException($"{kind} handle ({index},{generation}) is stale or invalid.");
        return index;
    }

    /// <summary>Invalidate a slot. Returns false for an already-stale handle so double-destroy is a
    /// no-op rather than a second JS-side teardown.</summary>
    public bool Release(uint index, uint generation)
    {
        if (!IsAlive(index, generation)) return false;
        _alive[(int)index] = false;
        // Skip generation 0 on wraparound: it is the invalid sentinel for handle structs.
        unchecked { _generations[(int)index]++; }
        if (_generations[(int)index] == 0) _generations[(int)index] = 1u;
        _free.Push(index);
        return true;
    }
}
