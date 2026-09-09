using System.Numerics;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Pbr;

/// <summary>Prioritizes invalidated probes and a focus region while reserving a fair background sweep.</summary>
internal sealed class ProbeUpdateScheduler
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Update
    {
        public int Probe;
        public int Slot;
    }

    private Update[] _updates = [];
    private bool[] _pending = [];
    private int _cursor;
    private bool _alternate;
    private readonly PriorityQueue<int, (int Selected, float Distance, int Index)> _nearest = new();
    private (int Element, (int Selected, float Distance, int Index) Priority)[] _candidates = [];

    public ReadOnlySpan<Update> Updates => _updates;
    public int SelectedCount { get; private set; }

    public void Reset(int count)
    {
        if (_updates.Length != count)
        {
            _updates = new Update[count];
            _pending = new bool[count];
            _candidates = new (int, (int, float, int))[count];
        }
        Array.Fill(_pending, true);
        _cursor = 0;
        _alternate = false;
        _nearest.EnsureCapacity(count);
    }

    public void Invalidate(int probe) => _pending[probe] = true;

    public int Select(ProbeGrid grid, int budget, Vector3? focus)
    {
        var count = _updates.Length;
        budget = budget <= 0 ? count : Math.Min(budget, count);
        for (var i = 0; i < count; i++) _updates[i].Slot = -1;
        SelectedCount = 0;
        if (budget == count)
        {
            for (var i = 0; i < count; i++) Add(i);
            _cursor = 0;
            return SelectedCount;
        }

        // New planes and explicitly invalidated regions are not usable until first traced.
        for (var i = 0; i < count && SelectedCount < budget; i++)
        {
            var probe = (_cursor + i) % count;
            if (_pending[probe]) Add(probe);
        }

        var remaining = budget - SelectedCount;
        var background = focus is null ? remaining : remaining / 2 + (remaining % 2 != 0 && _alternate ? 1 : 0);
        _alternate = !_alternate;
        for (var scanned = 0; scanned < count && background > 0; scanned++)
        {
            var probe = _cursor;
            _cursor = (_cursor + 1) % count;
            if (_updates[probe].Slot >= 0) continue;
            Add(probe);
            background--;
        }

        if (SelectedCount < budget && focus is { } position)
        {
            _nearest.Clear();
            for (var i = 0; i < count; i++)
                _candidates[i] = (i, (_updates[i].Slot >= 0 ? 1 : 0, Vector3.DistanceSquared(grid.Position(i), position), i));
            // EnqueueRange heapifies the reused array in linear time when the queue is empty.
            _nearest.EnqueueRange(_candidates);
            while (SelectedCount < budget && _nearest.TryDequeue(out var probe, out _)) Add(probe);
        }
        return SelectedCount;
    }

    private void Add(int probe)
    {
        _updates[SelectedCount].Probe = probe;
        _updates[probe].Slot = SelectedCount++;
        _pending[probe] = false;
    }
}
