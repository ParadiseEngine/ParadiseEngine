namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Shares an entry while leases exist and evicts it when its last owner releases it.</summary>
internal sealed class RefCountedCache<TKey, TValue>(Action<TValue>? release = null) where TKey : notnull where TValue : class
{
    private readonly Dictionary<TKey, Entry> _entries = [];

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
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry(key, factory());
            _entries.Add(key, entry);
        }
        return new Lease(this, entry);
    }

    public void Clear()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Retired = true;
            release?.Invoke(entry.Value);
        }
        _entries.Clear();
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

/// <summary>Keeps native resource dependencies cached until the owning resource is released.</summary>
internal sealed class NativeResource<T>(T native, List<IDisposable> dependencies) : IDisposable where T : class
{
    public T Native { get; } = native;

    public void Dispose()
    {
        foreach (var dependency in dependencies) dependency.Dispose();
        dependencies.Clear();
    }
}
