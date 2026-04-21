using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Paradise.ECS;

/// <summary>
/// Thread-safe entity ID allocator using atomic operations.
/// Reserves entity IDs with versions for use by <see cref="EntityCommandBuffer"/>
/// without requiring access to <see cref="EntityManager"/>.
/// </summary>
public sealed class EntityIdAllocator
{
    private int _nextFreshId;
    private readonly ConcurrentStack<(int Id, uint Version)> _freeSlots = new();
    private readonly int _maxEntityId;

    /// <summary>
    /// Creates a new EntityIdAllocator with the specified maximum entity ID.
    /// </summary>
    /// <param name="maxEntityId">The maximum entity ID that can be allocated.
    /// Use <see cref="int.MaxValue"/> for no practical limit.</param>
    public EntityIdAllocator(int maxEntityId = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxEntityId);
        _maxEntityId = maxEntityId;
    }

    /// <summary>
    /// Atomically reserves an entity ID with its version. Thread-safe.
    /// Fresh IDs get version 1. Reused IDs get pre-computed next version.
    /// </summary>
    /// <returns>An entity with a reserved ID and version.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the next fresh ID would exceed the maximum entity ID.</exception>
    public Entity Reserve()
    {
        if (_freeSlots.TryPop(out var slot))
            return new Entity(slot.Id, slot.Version);

        int id = Interlocked.Increment(ref _nextFreshId) - 1;
        if (id > _maxEntityId)
        {
            // Roll back the increment so the allocator stays consistent
            Interlocked.Decrement(ref _nextFreshId);
            throw new InvalidOperationException(
                $"Entity ID {id} exceeds the maximum allowed entity ID {_maxEntityId}.");
        }
        return new Entity(id, 1);
    }

    /// <summary>
    /// Returns a reserved or destroyed entity ID to the free pool.
    /// The caller must provide the next version to use when the ID is reused.
    /// </summary>
    /// <param name="id">The entity ID to release.</param>
    /// <param name="nextVersion">The version to assign when this ID is next reserved.</param>
    public void Release(int id, uint nextVersion)
    {
        _freeSlots.Push((id, nextVersion));
    }

    /// <summary>
    /// Returns the ID that would be assigned to the next fresh entity,
    /// without actually reserving it. Not reliable under concurrent access.
    /// </summary>
    /// <returns>The next fresh entity ID.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekNextFreshId() => Volatile.Read(ref _nextFreshId);

    /// <summary>
    /// Returns the ID that would be assigned by the next <see cref="Reserve"/> call,
    /// considering free slots. Not reliable under concurrent access.
    /// </summary>
    /// <returns>The next entity ID that would be reserved.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekNextId()
    {
        if (_freeSlots.TryPeek(out var slot))
            return slot.Id;
        return Volatile.Read(ref _nextFreshId);
    }

    /// <summary>
    /// Resets the allocator to its initial state.
    /// </summary>
    public void Clear()
    {
        _freeSlots.Clear();
        Volatile.Write(ref _nextFreshId, 0);
    }

    /// <summary>
    /// Copies state from the source allocator to this allocator.
    /// Not thread-safe — must be called when no concurrent access is occurring.
    /// </summary>
    /// <param name="source">The source allocator to copy from.</param>
    internal void CopyFrom(EntityIdAllocator source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _nextFreshId = source._nextFreshId;
        _freeSlots.Clear();
        int count = source._freeSlots.Count;
        if (count > 0)
        {
            var items = ArrayPool<(int, uint)>.Shared.Rent(count);
            try
            {
                // CopyTo writes LIFO order (top→bottom). Reverse + PushRange
                // restores the original pop order in a single bulk operation.
                source._freeSlots.CopyTo(items, 0);
                Array.Reverse(items, 0, count);
                _freeSlots.PushRange(items, 0, count);
            }
            finally
            {
                ArrayPool<(int, uint)>.Shared.Return(items);
            }
        }
    }
}
