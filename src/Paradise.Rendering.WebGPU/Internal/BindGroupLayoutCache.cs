using System;
using System.Collections.Generic;
using WgBindGroupLayout = WebGpuSharp.BindGroupLayout;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Content-keyed cache of native <see cref="WgBindGroupLayout"/> instances below the
/// public <see cref="BindGroupLayoutHandle"/> layer. Mirrors <see cref="PipelineCache"/>'s design:
/// two callers with structurally-equal <see cref="BindGroupLayoutDesc"/> records share the native
/// layout; each caller gets a distinct public handle whose destruction never yanks the shared
/// native out from under the other.
/// <para>
/// Append-only for the renderer's lifetime — by design, mirroring <see cref="PipelineCache"/>.
/// Bind group layouts are typically built up-front and live to renderer shutdown; the small
/// retained-native cost trades cleanly for a predictable handle-ownership contract. Revisit if
/// hot-reload / dynamic-shader scenarios surface measurable layout churn (refcount or LRU
/// eviction would land then).
/// </para></summary>
internal sealed class BindGroupLayoutCache
{
    private readonly Dictionary<Key, WgBindGroupLayout> _byKey = new();

    /// <summary>Cache key — snapshots the descriptor's <see cref="BindGroupLayoutDesc.Entries"/>
    /// array contents into an internal copy at construction so caller-side post-insertion
    /// mutations cannot poison the dictionary's hash/equality contract. The original
    /// <see cref="BindGroupLayoutDesc"/> is reference-typed and its <c>Entries</c> array is also
    /// reference-typed; without snapshotting, a caller mutating the array between insert and
    /// lookup would break dictionary lookups for every entry sharing that backing array.</summary>
    private readonly struct Key : IEquatable<Key>
    {
        public readonly uint GroupIndex;
        public readonly BindGroupLayoutEntryDesc[] Entries;

        public Key(BindGroupLayoutDesc desc)
        {
            GroupIndex = desc.GroupIndex;
            // Defensive copy. Entry records themselves are immutable (sealed records with init-only
            // properties), so a shallow array copy is sufficient.
            var src = desc.Entries;
            var copy = new BindGroupLayoutEntryDesc[src.Length];
            Array.Copy(src, copy, src.Length);
            // Sort by binding number so the cache hits regardless of caller-side entry order.
            // The reflection loader sorts before insertion; the public CreateBindGroupLayout API
            // doesn't, so unsorted callers would otherwise miss the cache on structurally-equal
            // layouts. Snapshot + sort in the Key keeps comparison + hash uniform across paths.
            Array.Sort(copy, static (a, b) => a.Binding.CompareTo(b.Binding));
            Entries = copy;
        }

        public bool Equals(Key other)
        {
            if (GroupIndex != other.GroupIndex) return false;
            if (Entries.Length != other.Entries.Length) return false;
            for (var i = 0; i < Entries.Length; i++)
            {
                if (Entries[i].Binding != other.Entries[i].Binding) return false;
                if (Entries[i].Visibility != other.Entries[i].Visibility) return false;
                if (Entries[i].Type != other.Entries[i].Type) return false;
                if (Entries[i].MinBufferSize != other.Entries[i].MinBufferSize) return false;
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is Key k && Equals(k);

        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(GroupIndex);
            h.Add(Entries.Length);
            for (var i = 0; i < Entries.Length; i++)
            {
                h.Add(Entries[i].Binding);
                h.Add(Entries[i].Visibility);
                h.Add(Entries[i].Type);
                h.Add(Entries[i].MinBufferSize);
            }
            return h.ToHashCode();
        }
    }

    public int Count => _byKey.Count;

    public WgBindGroupLayout GetOrCreateNative(BindGroupLayoutDesc desc, Func<BindGroupLayoutDesc, WgBindGroupLayout> factory)
    {
        var key = new Key(desc);
        if (_byKey.TryGetValue(key, out var native)) return native;
        var created = factory(desc);
        _byKey[key] = created;
        return created;
    }

    public void Clear() => _byKey.Clear();
}
