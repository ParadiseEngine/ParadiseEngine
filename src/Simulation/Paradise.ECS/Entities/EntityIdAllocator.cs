using System.Runtime.CompilerServices;

namespace Paradise.ECS;

/// <summary>
/// Thread-safe entity ID allocator using atomic operations.
/// Backs <see cref="EntityManager"/> entity creation with fresh-ID and free-slot reuse.
/// </summary>
/// <remarks>Released IDs are a LIFO array guarded by a monitor. A concurrent stack allocated a node
/// per release and per copied slot, and snapshot hosts copy every freed ID into a world each tick.</remarks>
public sealed class EntityIdAllocator
{
    private int _nextFreshId;
    private readonly object _freeGate = new();
    private (int Id, uint Version)[] _freeSlots = [];
    private int _freeCount;
    private readonly int _maxEntityId;

    /// <summary>Creates a new EntityIdAllocator with the specified maximum entity ID.</summary>
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
        // With nothing free (read without the monitor) the fresh path stays lock-free; a release racing
        // the read may be missed, as a pop racing a push could miss it.
        if (Volatile.Read(ref _freeCount) > 0)
        {
            lock (_freeGate)
            {
                if (_freeCount > 0)
                {
                    var slot = _freeSlots[--_freeCount];
                    return new Entity(slot.Id, slot.Version);
                }
            }
        }

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
        lock (_freeGate)
        {
            if (_freeCount == _freeSlots.Length) Array.Resize(ref _freeSlots, Math.Max(16, _freeSlots.Length * 2));
            _freeSlots[_freeCount++] = (id, nextVersion);
        }
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
    public int PeekNextId()
    {
        lock (_freeGate)
        {
            if (_freeCount > 0) return _freeSlots[_freeCount - 1].Id;
        }
        return Volatile.Read(ref _nextFreshId);
    }

    /// <summary>Resets the allocator to its initial state.</summary>
    /// <remarks>Call only when no reservations or releases can run concurrently.</remarks>
    public void Clear()
    {
        lock (_freeGate) _freeCount = 0;
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
        // The slots keep their order, so the copy reuses IDs in the order the source would.
        if (_freeSlots.Length < source._freeCount) _freeSlots = new (int, uint)[source._freeSlots.Length];
        Array.Copy(source._freeSlots, _freeSlots, source._freeCount);
        _freeCount = source._freeCount;
    }
}
