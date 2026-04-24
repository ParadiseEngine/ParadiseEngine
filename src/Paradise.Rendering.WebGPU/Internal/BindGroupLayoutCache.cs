using System;
using System.Collections.Generic;
using WgBindGroupLayout = WebGpuSharp.BindGroupLayout;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Content-keyed cache of native <see cref="WgBindGroupLayout"/> instances below the
/// public <see cref="BindGroupLayoutHandle"/> layer. Mirrors <see cref="PipelineCache"/>'s design:
/// two callers with structurally-equal <see cref="BindGroupLayoutDesc"/> records share the native
/// layout; each caller gets a distinct public handle whose destruction never yanks the shared
/// native out from under the other.</summary>
internal sealed class BindGroupLayoutCache
{
    private readonly Dictionary<Key, WgBindGroupLayout> _byKey = new();

    private readonly struct Key : IEquatable<Key>
    {
        public readonly BindGroupLayoutDesc Desc;
        public Key(BindGroupLayoutDesc desc) { Desc = desc; }

        public bool Equals(Key other)
        {
            if (Desc.GroupIndex != other.Desc.GroupIndex) return false;
            var a = Desc.Entries;
            var b = other.Desc.Entries;
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i].Binding != b[i].Binding) return false;
                if (a[i].Visibility != b[i].Visibility) return false;
                if (a[i].Type != b[i].Type) return false;
                if (a[i].MinBufferSize != b[i].MinBufferSize) return false;
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is Key k && Equals(k);

        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(Desc.GroupIndex);
            var entries = Desc.Entries;
            h.Add(entries.Length);
            for (var i = 0; i < entries.Length; i++)
            {
                h.Add(entries[i].Binding);
                h.Add(entries[i].Visibility);
                h.Add(entries[i].Type);
                h.Add(entries[i].MinBufferSize);
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
