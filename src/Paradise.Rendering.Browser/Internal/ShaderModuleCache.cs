namespace Paradise.Rendering.Browser.Internal;

/// <summary>Shares shader modules while pipelines own leases and recycles retired JS slots.</summary>
internal sealed class ShaderModuleCache(Action<int, string, string> create, Action<int> destroy) : IDisposable
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Stack<int> _free = [];
    private int _nextSlot;
    private bool _disposed;

    public int Count => _entries.Count;

    public Lease Acquire(string wgsl, string label)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_entries.TryGetValue(wgsl, out var entry))
        {
            var slot = _free.TryPop(out var recycled) ? recycled : _nextSlot++;
            try { create(slot, wgsl, label); }
            catch { _free.Push(slot); throw; }
            entry = new Entry(wgsl, slot);
            _entries.Add(wgsl, entry);
        }
        entry.Users++;
        return new Lease(this, entry);
    }

    private void Release(Entry entry)
    {
        if (_disposed || --entry.Users != 0) return;
        _entries.Remove(entry.Wgsl);
        _free.Push(entry.Slot);
        destroy(entry.Slot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            foreach (var entry in _entries.Values) destroy(entry.Slot);
        }
        finally
        {
            _entries.Clear();
            _free.Clear();
        }
    }

    internal sealed class Entry(string wgsl, int slot)
    {
        public string Wgsl { get; } = wgsl;
        public int Slot { get; } = slot;
        public int Users { get; set; }
    }

    internal sealed class Lease(ShaderModuleCache owner, Entry entry) : IDisposable
    {
        private bool _disposed;
        public int Slot => entry.Slot;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.Release(entry);
        }
    }
}
