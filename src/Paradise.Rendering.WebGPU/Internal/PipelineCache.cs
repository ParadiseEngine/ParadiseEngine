using System;
using System.Collections.Generic;
using WgRenderPipeline = WebGpuSharp.RenderPipeline;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Internal cache of native WebGPUSharp <see cref="WgRenderPipeline"/> instances keyed
/// by <see cref="PipelineDesc.ContentHash"/>. Lives below the public <see cref="PipelineHandle"/>
/// layer: each call to <see cref="GetOrCreateNative"/> returns a shared native pipeline reference,
/// but the handle minting + slot-table allocation happens above this cache so that two
/// <c>CreatePipeline</c> calls with structurally-equal descriptors get distinct
/// <see cref="PipelineHandle"/> values. Destroying one of those handles never invalidates the
/// other — the underlying native pipeline stays live until the renderer disposes.</summary>
/// <remarks>M1 keeps cache entries for the renderer's lifetime (no refcount, no eviction). M2/M3
/// will revisit when dynamic pipeline rebuilds become common; the cache will need a refcount or
/// LRU eviction at that point. The current design intentionally trades a small amount of native
/// memory for a clean public-handle contract that matches every other resource type.</remarks>
internal sealed class PipelineCache
{
    private readonly Dictionary<int, Entry> _byHash = new();
    private readonly List<WgRenderPipeline> _all = new();

    private readonly struct Entry
    {
        public readonly PipelineDesc Desc;
        public readonly WgRenderPipeline Native;
        public Entry(in PipelineDesc desc, WgRenderPipeline native) { Desc = desc; Native = native; }
    }

    /// <summary>Get a cached native pipeline for <paramref name="desc"/>, or invoke
    /// <paramref name="factory"/> to create one. Insert-only: a hash collision with a
    /// structurally-different descriptor throws — the cache is the canonical store for native
    /// pipelines, overwriting would orphan the displaced native resource.</summary>
    public WgRenderPipeline GetOrCreateNative(in PipelineDesc desc, Func<PipelineDesc, WgRenderPipeline> factory)
    {
        var hash = desc.ContentHash();
        if (_byHash.TryGetValue(hash, out var entry))
        {
            if (entry.Desc.Equals(desc)) return entry.Native;
            // Hash collision with a different desc — treated as programmer error. Cache cannot
            // silently overwrite (orphans the displaced native pipeline) or evict via callback
            // (leaks a "caller must release" contract into the public surface). M1's PipelineDesc
            // surface is small enough that real collisions are vanishingly unlikely; if one occurs
            // it warrants investigation, not silent fall-through.
            throw new InvalidOperationException(
                $"PipelineCache: hash collision (0x{hash:X8}) between two structurally-different " +
                $"PipelineDesc instances. Investigate the descriptor difference rather than " +
                $"replacing one transparently.");
        }
        var created = factory(desc);
        _byHash[hash] = new Entry(in desc, created);
        _all.Add(created);
        return created;
    }

    public bool TryGetNative(in PipelineDesc desc, out WgRenderPipeline native)
    {
        var hash = desc.ContentHash();
        if (_byHash.TryGetValue(hash, out var entry) && entry.Desc.Equals(desc))
        {
            native = entry.Native;
            return true;
        }
        native = null!;
        return false;
    }

    public int Count => _byHash.Count;

    /// <summary>Drop every cached native pipeline reference. Called at renderer disposal so
    /// finalizers can release the native handles in the next GC cycle.</summary>
    public void Clear()
    {
        _byHash.Clear();
        _all.Clear();
    }
}
