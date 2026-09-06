using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Paradise.Rendering.Graph;

/// <summary>Bind groups keyed by what they bind, so a pass asks for one by content every frame
/// and gets the same handle back while nothing it binds has changed.
///
/// <para>This replaces the rebuild-on-resize discipline: a group over a recreated texture has a
/// different key, so it is simply a different group, and the old one ages out. A group not asked
/// for within a few frames is destroyed — that is what happens to a culled feature's groups, and
/// re-enabling the feature recreates them at the cost of one creation.</para></summary>
public sealed class BindGroupCache : IDisposable
{
    /// <summary>Frames a group may go unrequested before it is destroyed. Small so a resize does
    /// not hold groups over destroyed views for long; more than one so a feature toggled off and
    /// on within a frame or two pays nothing.</summary>
    private const int RetainFrames = 3;

    private readonly IBindGroupFactory _factory;
    private readonly Dictionary<Key, Entry> _groups = [];
    private readonly List<Key> _expired = [];
    private int _frame;
    private bool _disposed;

    public BindGroupCache(IBindGroupFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public int Count => _groups.Count;

    /// <summary>The group binding exactly <paramref name="entries"/> to <paramref name="layout"/>,
    /// created on first request. <paramref name="layout"/> is compared by identity: a program's
    /// reflected layouts are stable objects, and equality by content would cost a walk per lookup
    /// to distinguish nothing.</summary>
    public BindGroupHandle Get(string name, BindGroupLayoutDesc layout, ReadOnlySpan<BindGroupEntryDesc> entries)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = new Key(layout, entries);
        // Create before inserting: a creation that throws must not leave a default handle behind
        // that every later frame would hand to the recorder as if it were the group.
        if (!_groups.TryGetValue(key, out var entry))
        {
            entry.Handle = _factory.CreateBindGroup(new BindGroupDesc(name, layout, entries.ToArray()));
        }
        entry.LastUsedFrame = _frame;
        _groups[key] = entry;
        return entry.Handle;
    }

    /// <summary>Close the frame: destroy every group nobody asked for recently.</summary>
    public void EndFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _frame++;

        foreach (var (key, entry) in _groups)
            if (_frame - entry.LastUsedFrame > RetainFrames)
                _expired.Add(key);
        foreach (var key in _expired)
        {
            _factory.DestroyBindGroup(_groups[key].Handle);
            _groups.Remove(key);
        }
        _expired.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _groups.Values) _factory.DestroyBindGroup(entry.Handle);
        _groups.Clear();
    }

    private struct Entry
    {
        public BindGroupHandle Handle;
        public int LastUsedFrame;
    }

    [InlineArray(MaxEntries)]
    private struct EntrySlots
    {
        private BindGroupEntryDesc _slot0;
    }

    internal const int MaxEntries = 16;

    private readonly struct Key : IEquatable<Key>
    {
        private readonly BindGroupLayoutDesc _layout;
        private readonly int _count;
        private readonly EntrySlots _entries;

        public Key(BindGroupLayoutDesc layout, ReadOnlySpan<BindGroupEntryDesc> entries)
        {
            if (entries.Length > MaxEntries)
                throw new ArgumentOutOfRangeException(nameof(entries), $"A bind group of {entries.Length} entries exceeds the cache's {MaxEntries}.");
            _layout = layout;
            _count = entries.Length;
            entries.CopyTo(_entries);
        }

        public bool Equals(Key other)
        {
            if (!ReferenceEquals(_layout, other._layout) || _count != other._count) return false;
            for (var i = 0; i < _count; i++)
                if (_entries[i] != other._entries[i]) return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is Key other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(RuntimeHelpers.GetHashCode(_layout));
            hash.Add(_count);
            for (var i = 0; i < _count; i++) hash.Add(_entries[i]);
            return hash.ToHashCode();
        }
    }
}
