using Paradise.Rendering.Internal;

namespace Paradise.Rendering.Browser.Internal;

internal readonly record struct ShaderModuleSlot(int Index);

/// <summary>Shares shader modules while pipelines own leases and recycles retired JS slots.</summary>
internal sealed class ShaderModuleCache : IDisposable
{
    private readonly RefCountedCache<string, ShaderModuleSlot> _entries;
    private readonly Action<int, string, string> _create;
    private readonly Action<int> _destroy;
    private readonly Stack<int> _free = [];
    private int _nextSlot;

    public ShaderModuleCache(Action<int, string, string> create, Action<int> destroy)
    {
        _create = create;
        _destroy = destroy;
        _entries = new(Release, StringComparer.Ordinal);
    }

    public int Count => _entries.Count;

    public RefCountedCache<string, ShaderModuleSlot>.Lease Acquire(string wgsl, string label) =>
        _entries.Acquire(wgsl, () => Create(wgsl, label));

    private ShaderModuleSlot Create(string wgsl, string label)
    {
        var slot = _free.TryPop(out var recycled) ? recycled : _nextSlot++;
        try { _create(slot, wgsl, label); }
        catch { _free.Push(slot); throw; }
        return new ShaderModuleSlot(slot);
    }

    private void Release(ShaderModuleSlot slot)
    {
        // A failed JS release must not make its potentially live slot available for reuse.
        _destroy(slot.Index);
        _free.Push(slot.Index);
    }

    public void Dispose()
    {
        try { _entries.Dispose(); }
        finally { _free.Clear(); }
    }
}
