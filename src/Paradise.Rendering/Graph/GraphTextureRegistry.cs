using System;
using System.Collections.Generic;

namespace Paradise.Rendering.Graph;

/// <summary>The frame's render targets, owned by name.
///
/// <para>A renderer used to hold one field per target — the texture, its sampled view, the
/// per-layer views — and a resize was a hand-ordered list of destroys and recreates that each
/// bind group had to be rebuilt after. Here a target is declared by <see cref="Ensure"/> with the
/// descriptor it should have; the call is idempotent, so the same declaration every resize
/// recreates only what changed and leaves every bind group over an unchanged target valid.
/// Views are minted on demand and cached with the texture, so two readers of one target share
/// one view and a view never outlives its texture.</para>
///
/// <para>Ownership is what makes a resource's scope knowable: a target that lives here is private
/// to the graph unless <see cref="Export"/> hands it out, which is the fact
/// <see cref="FrameGraph"/> used to have to be told with <see cref="GraphResourceScope"/>.</para></summary>
public sealed class GraphTextureRegistry : IDisposable
{
    private readonly ITextureFactory _factory;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    public GraphTextureRegistry(ITextureFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <summary>Declare that <paramref name="name"/> is a texture shaped like <paramref name="desc"/>.
    /// Returns true when the texture was created or recreated — the signal that bind groups over it
    /// must be rebuilt. The descriptor's own name is replaced by <paramref name="name"/>, so the
    /// registry key is also the label a GPU debugger shows.</summary>
    public bool Ensure(string name, TextureDesc desc)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObjectDisposedException.ThrowIf(_disposed, this);

        desc = desc with { Name = name };
        if (_entries.TryGetValue(name, out var existing))
        {
            if (existing.Desc == desc) return false;
            existing.Destroy(_factory);
            existing.Desc = desc;
            existing.Texture = _factory.CreateTexture(in desc);
            return true;
        }

        _entries.Add(name, new Entry(desc, _factory.CreateTexture(in desc)));
        return true;
    }

    public bool Contains(string name) => _entries.ContainsKey(name);

    /// <summary>The declared shape of <paramref name="name"/>.</summary>
    public TextureDesc DescriptorOf(string name) => Get(name).Desc;

    public TextureHandle Texture(string name) => Get(name).Texture;

    /// <summary>A 2D view over layer 0 — the view a single-layer target is sampled and rendered
    /// through.</summary>
    public TextureViewHandle View(string name) => SliceView(name, 0);

    /// <summary>A 2D view over one layer, for rendering into that layer of an array.</summary>
    public TextureViewHandle SliceView(string name, uint layer)
    {
        var entry = Get(name);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(layer, entry.Desc.DepthOrArrayLayers);

        entry.Slices ??= new TextureViewHandle[entry.Desc.DepthOrArrayLayers];
        ref var view = ref entry.Slices[layer];
        if (!view.IsValid)
        {
            view = _factory.CreateTextureView(new TextureViewDesc(
                $"{name}.Layer{layer}", entry.Texture, TextureViewDimension.D2, layer, 1));
        }
        return view;
    }

    /// <summary>A 2D-array view over every layer — what a shader that declares an array texture
    /// samples, even when the array has one layer.</summary>
    public TextureViewHandle ArrayView(string name)
    {
        var entry = Get(name);
        if (!entry.ArrayView.IsValid)
        {
            entry.ArrayView = _factory.CreateTextureView(new TextureViewDesc(
                $"{name}.Array", entry.Texture, TextureViewDimension.D2Array, 0, entry.Desc.DepthOrArrayLayers));
        }
        return entry.ArrayView;
    }

    /// <summary>Mark <paramref name="name"/> as visible outside the graph — handed to a host or a
    /// game's material — so a pass writing it is never culled. Sticks to the name across
    /// recreation, because the outside reader keeps asking for it by name.</summary>
    public void Export(string name) => Get(name).Exported = true;

    public bool IsExported(string name) => Get(name).Exported;

    /// <summary>Destroy <paramref name="name"/> and every view over it. A missing name is not an
    /// error, so a disable path need not track whether its enable path ran.</summary>
    public void Release(string name)
    {
        if (!_entries.Remove(name, out var entry)) return;
        entry.Destroy(_factory);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries.Values) entry.Destroy(_factory);
        _entries.Clear();
    }

    private Entry Get(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _entries.TryGetValue(name, out var entry)
            ? entry
            : throw new KeyNotFoundException($"No target named '{name}' has been declared with Ensure.");
    }

    private sealed class Entry(TextureDesc desc, TextureHandle texture)
    {
        public TextureDesc Desc = desc;
        public TextureHandle Texture = texture;
        public TextureViewHandle[]? Slices;
        public TextureViewHandle ArrayView;
        public bool Exported;

        public void Destroy(ITextureFactory factory)
        {
            if (Slices is not null)
            {
                foreach (var slice in Slices)
                    if (slice.IsValid) factory.DestroyTextureView(slice);
                Slices = null;
            }
            if (ArrayView.IsValid) factory.DestroyTextureView(ArrayView);
            ArrayView = default;
            factory.DestroyTexture(Texture);
            Texture = default;
        }
    }
}
