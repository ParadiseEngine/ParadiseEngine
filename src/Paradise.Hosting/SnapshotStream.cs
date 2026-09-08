using System.Diagnostics.CodeAnalysis;

namespace Paradise.Hosting;

/// <summary>Associates an immutable published world with its completed simulation frame.</summary>
public readonly record struct WorldSnapshot<T>(T World, long Frame) where T : class;

/// <summary>Transfers published worlds to a consumer until it returns them for reuse.</summary>
public interface ISnapshotStream<T> where T : class
{
    /// <summary>Borrows the oldest available snapshot until its world is recycled.</summary>
    bool TryRead(out WorldSnapshot<T> snapshot);

    /// <summary>Returns a borrowed world exactly once after all consumer reads have finished.</summary>
    void Recycle(T world);
}

/// <summary>Coordinates bounded snapshot delivery and producer-owned world reuse.</summary>
/// <remarks>The producer owns world allocation and disposal, and must stop consumers before
/// disposing worlds. The newest published world remains reserved for the producer's next
/// simulation read, even if a consumer returns it before another snapshot is published.</remarks>
public sealed class SnapshotStream<T> : ISnapshotStream<T> where T : class
{
    private enum Ownership
    {
        Queued,
        Borrowed,
        Retained,
        Recycled,
    }

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Queue<WorldSnapshot<T>> _pending;
    private readonly Queue<T> _recycled;
    private readonly Dictionary<T, Ownership> _ownership;
    private T? _newest;

    public SnapshotStream(int capacity = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _pending = new Queue<WorldSnapshot<T>>(capacity);
        _recycled = new Queue<T>(capacity);
        _ownership = new Dictionary<T, Ownership>(capacity, ReferenceEqualityComparer.Instance);
    }

    /// <summary>Publishes a world and returns excess unread snapshots to the producer pool.</summary>
    /// <remarks>A world must be newly allocated or acquired through <see cref="TryTakeRecycled"/>.
    /// Publishing another world releases the previous snapshot's reservation as simulation input.</remarks>
    public void Publish(T world, long frame)
    {
        ArgumentNullException.ThrowIfNull(world);
        lock (_gate)
        {
            if (!_ownership.TryAdd(world, Ownership.Queued))
            {
                throw new InvalidOperationException("The world is already owned by this snapshot stream.");
            }

            if (_newest is not null && _ownership[_newest] == Ownership.Retained)
            {
                ReturnToProducer(_newest);
            }

            _newest = world;
            _pending.Enqueue(new WorldSnapshot<T>(world, frame));
            while (_pending.Count > _capacity)
            {
                ReturnToProducer(_pending.Dequeue().World);
            }
        }
    }

    public bool TryRead(out WorldSnapshot<T> snapshot)
    {
        lock (_gate)
        {
            if (!_pending.TryDequeue(out snapshot))
            {
                return false;
            }

            _ownership[snapshot.World] = Ownership.Borrowed;
            return true;
        }
    }

    public void Recycle(T world)
    {
        ArgumentNullException.ThrowIfNull(world);
        lock (_gate)
        {
            if (!_ownership.TryGetValue(world, out var ownership) || ownership != Ownership.Borrowed)
            {
                throw new InvalidOperationException("Only a currently borrowed snapshot world can be recycled.");
            }

            if (ReferenceEquals(world, _newest))
            {
                _ownership[world] = Ownership.Retained;
            }
            else
            {
                ReturnToProducer(world);
            }
        }
    }

    /// <summary>Acquires a world no longer used by any consumer or reserved as simulation input.</summary>
    public bool TryTakeRecycled([MaybeNullWhen(false)] out T world)
    {
        lock (_gate)
        {
            if (!_recycled.TryDequeue(out world))
            {
                return false;
            }

            _ownership.Remove(world);
            return true;
        }
    }

    private void ReturnToProducer(T world)
    {
        _ownership[world] = Ownership.Recycled;
        _recycled.Enqueue(world);
    }
}
