namespace Paradise.Rendering.Internal;

/// <summary>Shares an entry while leases exist and evicts it when its last owner releases it.</summary>
/// <remarks>Cache and lease operations belong to the owning render thread.</remarks>
internal sealed class RefCountedCache<TKey, TValue>(Action<TValue>? release = null,
    IEqualityComparer<TKey>? comparer = null) : IDisposable where TKey : notnull
{
    private readonly Dictionary<TKey, Entry> _entries = new(comparer);
    private bool _disposed;

    internal sealed class Entry(TKey key, TValue value)
    {
        public readonly TKey Key = key;
        public readonly TValue Value = value;
        public int References;
        public bool Retired;
    }

    public int Count => _entries.Count;

    public Lease Acquire(TKey key, Func<TValue> factory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry(key, factory());
            _entries.Add(key, entry);
        }
        return new Lease(this, entry);
    }

    /// <summary>Invalidates current leases and releases their values while leaving the cache reusable.</summary>
    public void Clear()
    {
        if (_entries.Count == 0) return;
        Entry[] entries = [.. _entries.Values];
        _entries.Clear();
        // A cleanup callback may inspect old leases or acquire a replacement under the same key.
        foreach (var entry in entries) entry.Retired = true;

        List<Exception>? failures = null;
        foreach (var entry in entries)
        {
            try { release?.Invoke(entry.Value); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is not null) throw new AggregateException("Cached resource cleanup failed.", failures);
    }

    /// <summary>Closes the cache permanently before releasing all current values.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
    }

    private void Release(Entry entry)
    {
        if (entry.Retired || --entry.References != 0) return;
        _entries.Remove(entry.Key);
        entry.Retired = true;
        release?.Invoke(entry.Value);
    }

    internal sealed class Lease : IDisposable
    {
        private RefCountedCache<TKey, TValue>? _owner;
        private Entry? _entry;

        internal Lease(RefCountedCache<TKey, TValue> owner, Entry entry)
        {
            _owner = owner;
            _entry = entry;
            entry.References++;
        }

        public TValue Value => _entry is { Retired: false } entry
            ? entry.Value : throw new ObjectDisposedException(nameof(Lease));

        public Lease Retain()
        {
            ObjectDisposedException.ThrowIf(_entry is null || _entry.Retired, this);
            return new Lease(_owner!, _entry);
        }

        public void Dispose()
        {
            if (_entry is not { } entry) return;
            var owner = _owner!;
            _entry = null;
            _owner = null;
            owner.Release(entry);
        }
    }
}
