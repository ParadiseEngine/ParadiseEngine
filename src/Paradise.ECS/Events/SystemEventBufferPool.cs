using System.Runtime.InteropServices;

namespace Paradise.ECS;

/// <summary>
/// Pool of <see cref="SystemEventWriter"/> instances handed out in SCHEDULE order — the event-side
/// sibling of <see cref="EntityCommandBufferPool"/>. <see cref="SystemSchedule{TMask,TConfig}"/>
/// rents one writer per work item at dispatch time (on the schedule thread, in schedule order);
/// each writer runs on one thread and appends sequentially, so it is internally deterministic.
/// <see cref="CommitTo"/> then merges the writers into the world's event store in rent (= schedule)
/// order, so the delivered event order is identical under any wave scheduler. See
/// <c>docs/system-events.md</c>.
/// </summary>
internal sealed class SystemEventBufferPool
{
    private readonly List<SystemEventWriter> _writers = new();
    private int _rentedCount;

    /// <summary>Returns the next writer in schedule order, creating one on first use.</summary>
    public SystemEventWriter Rent()
    {
        if (_rentedCount == _writers.Count)
            _writers.Add(new SystemEventWriter());
        return _writers[_rentedCount++];
    }

    /// <summary>Merges all writers rented this run into <paramref name="store"/>, in rent order.</summary>
    public void CommitTo(WorldEventStore store)
    {
        store.Commit(CollectionsMarshal.AsSpan(_writers)[.._rentedCount]);
    }

    /// <summary>Clears all rented writers and resets the rent cursor for the next run.</summary>
    public void ClearAll()
    {
        for (int i = 0; i < _rentedCount; i++)
            _writers[i].Clear();
        _rentedCount = 0;
    }
}
