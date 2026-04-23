using System;
using System.Collections.Generic;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Caches <see cref="PipelineHandle"/>s keyed by <see cref="PipelineDesc.ContentHash"/>.
/// A second <see cref="GetOrCreate"/> with the same content returns the cached handle instead of
/// allocating a new pipeline. Removed handles drop their cache entry so a re-create with the same
/// content compiles a fresh pipeline.</summary>
internal sealed class PipelineCache
{
    private readonly Dictionary<int, Entry> _byHash = new();

    private readonly struct Entry
    {
        public readonly PipelineDesc Desc;
        public readonly PipelineHandle Handle;
        public Entry(in PipelineDesc desc, PipelineHandle handle) { Desc = desc; Handle = handle; }
    }

    public PipelineHandle GetOrCreate(in PipelineDesc desc, Func<PipelineDesc, PipelineHandle> factory)
    {
        var hash = desc.ContentHash();
        if (_byHash.TryGetValue(hash, out var entry))
        {
            if (entry.Desc.Equals(desc)) return entry.Handle;
            // Insert-only semantics: a 32-bit hash collision with a structurally-different
            // descriptor is treated as a programmer error. The cache is not authorized to
            // silently overwrite (would orphan the displaced native pipeline) or to evict via
            // callback (would leak a "caller must destroy displaced" contract into every consumer
            // — see PR #55 review thread). On collision, throw and let the caller use Forget()
            // first if they really intend to replace.
            throw new InvalidOperationException(
                $"PipelineCache: hash collision (0x{hash:X8}) between two structurally-different " +
                $"PipelineDesc instances. The cache is insert-only — call Forget() on the existing " +
                $"handle before re-inserting if replacement is intended.");
        }
        var created = factory(desc);
        _byHash[hash] = new Entry(in desc, created);
        return created;
    }

    public bool TryGet(in PipelineDesc desc, out PipelineHandle handle)
    {
        var hash = desc.ContentHash();
        if (_byHash.TryGetValue(hash, out var entry) && entry.Desc.Equals(desc))
        {
            handle = entry.Handle;
            return true;
        }
        handle = PipelineHandle.Invalid;
        return false;
    }

    public void Forget(PipelineHandle handle)
    {
        // Linear scan — pipelines are few; the cost beats holding a reverse map that has to stay
        // consistent across hash collisions.
        int? toRemove = null;
        foreach (var kvp in _byHash)
        {
            if (kvp.Value.Handle.Equals(handle)) { toRemove = kvp.Key; break; }
        }
        if (toRemove is int key) _byHash.Remove(key);
    }

    public int Count => _byHash.Count;

    public void Clear() => _byHash.Clear();
}
