using System;
using System.Collections.Generic;
using WgRenderPipeline = WebGpuSharp.RenderPipeline;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Caches native pipelines by descriptor content hash.</summary>
/// <remarks>Public handles are allocated separately so destroying one does not invalidate another.
/// Native entries remain cached for the renderer lifetime, without eviction or reference
/// counting.</remarks>
internal sealed class PipelineCache
{
    private readonly Dictionary<int, Entry> _byHash = new();

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
            // Do not overwrite a colliding descriptor: the cache owns the displaced native pipeline
            // for the renderer lifetime.
            throw new InvalidOperationException(
                $"PipelineCache: hash collision (0x{hash:X8}) between two structurally-different " +
                $"PipelineDesc instances. Investigate the descriptor difference rather than " +
                $"replacing one transparently.");
        }
        var created = factory(desc);
        _byHash[hash] = new Entry(in desc, created);
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
    }
}
