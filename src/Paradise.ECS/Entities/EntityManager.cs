using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.ECS;

/// <summary>
/// Manager for entity lifecycle and location tracking.
/// Handles entity creation, destruction, validation, and archetype location using version-based handles.
/// Uses a contiguous list for packed entity metadata indexed by Entity.Id for O(1) lookups.
/// Single-threaded version without concurrent access support.
/// </summary>
public sealed class EntityManager : IEntityManager
{
    private readonly List<ulong> _packedLocations; // Uses packed EntityLocation format for memory efficiency
    private readonly EntityIdAllocator _allocator;
    private int _aliveCount; // Number of currently alive entities

    /// <summary>
    /// Creates a new EntityManager.
    /// </summary>
    /// <param name="initialCapacity">Initial capacity for entity storage.</param>
    /// <param name="maxEntityId">The maximum entity ID that can be allocated.</param>
    public EntityManager(int initialCapacity, int maxEntityId = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialCapacity, 0);
        _packedLocations = new List<ulong>(initialCapacity);
        _allocator = new EntityIdAllocator(maxEntityId);
    }

    /// <summary>
    /// Gets the number of currently alive entities.
    /// </summary>
    public int AliveCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _aliveCount;
    }

    /// <summary>
    /// Gets the current capacity of the entity storage.
    /// </summary>
    public int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _packedLocations.Count;
    }

    /// <summary>
    /// Gets the thread-safe entity ID allocator used by this manager.
    /// Can be shared with <see cref="EntityCommandBuffer"/> for real ID reservation.
    /// </summary>
    public EntityIdAllocator Allocator
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _allocator;
    }

    /// <summary>
    /// Returns the ID that would be assigned to the next created entity,
    /// without actually creating it. Used for validation before creation.
    /// </summary>
    /// <returns>The next entity ID that would be allocated.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekNextId()
    {
        return _allocator.PeekNextId();
    }

    /// <summary>
    /// Creates a new entity and returns a handle to it.
    /// The entity has no archetype until components are added.
    /// </summary>
    /// <returns>A valid entity handle.</returns>
    public Entity Create()
    {
        var entity = _allocator.Reserve();
        EnsureCapacity(entity.Id);
        _packedLocations[entity.Id] = new EntityLocation(entity.Version, -1, -1).Packed;
        _aliveCount++;
        return entity;
    }

    /// <summary>
    /// Registers a previously reserved entity (from <see cref="EntityIdAllocator.Reserve"/>)
    /// into the entity manager. Called during ECB playback to make reserved entities alive.
    /// </summary>
    /// <param name="entity">The reserved entity to register.</param>
    public void RegisterReserved(Entity entity)
    {
        EnsureCapacity(entity.Id);
        _packedLocations[entity.Id] = new EntityLocation(entity.Version, -1, -1).Packed;
        _aliveCount++;
    }

    /// <summary>
    /// Destroys the entity associated with the handle.
    /// Increments the version to invalidate the handle and returns the ID to the free pool.
    /// Safe to call multiple times or with invalid/stale handles (no-op).
    /// </summary>
    /// <param name="entity">The entity to destroy.</param>
    public void Destroy(Entity entity)
    {
        if (!entity.IsValid)
            return;

        if (entity.Id >= _packedLocations.Count)
            return;

        var location = EntityLocation.FromPacked(_packedLocations[entity.Id]);

        // Check if already destroyed (stale handle)
        if (location.Version != entity.Version)
            return;

        // Increment version to invalidate all existing handles, clear archetype info
        uint nextVersion = EntityLocation.NextVersion(location.Version);
        _packedLocations[entity.Id] = new EntityLocation(nextVersion, -1, -1).Packed;

        // Release to allocator's free pool
        _allocator.Release(entity.Id, nextVersion);
        _aliveCount--;
    }

    /// <summary>
    /// Checks if the entity is currently alive.
    /// </summary>
    /// <param name="entity">The entity to check.</param>
    /// <returns>True if the entity is alive, false if destroyed or invalid.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsAlive(Entity entity)
    {
        if (!entity.IsValid)
            return false;

        if (entity.Id >= _packedLocations.Count)
            return false;

        return EntityLocation.FromPacked(_packedLocations[entity.Id]).Version == entity.Version;
    }

    /// <summary>
    /// Gets the location for the specified entity ID.
    /// </summary>
    /// <param name="entityId">The entity ID.</param>
    /// <returns>The entity location.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntityLocation GetLocation(int entityId)
    {
        return EntityLocation.FromPacked(_packedLocations[entityId]);
    }

    /// <summary>
    /// Sets the location for the specified entity ID.
    /// </summary>
    /// <param name="entityId">The entity ID.</param>
    /// <param name="location">The new location.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetLocation(int entityId, in EntityLocation location)
    {
        _packedLocations[entityId] = location.Packed;
    }

    /// <summary>
    /// Ensures the list has enough elements for the given entity id.
    /// </summary>
    private void EnsureCapacity(int id)
    {
        int requiredCount = id + 1;
        if (requiredCount <= _packedLocations.Count)
            return;

        // Ensure internal capacity, then add default elements
        _packedLocations.EnsureCapacity(requiredCount);
        for (int i = _packedLocations.Count; i < requiredCount; i++)
        {
            _packedLocations.Add(default);
        }
    }

    /// <summary>
    /// Releases all resources used by this instance.
    /// </summary>
    public void Clear()
    {
        _allocator.Clear();
        _packedLocations.Clear();
        _aliveCount = 0;
    }

    /// <summary>
    /// Copies all entity state from the source manager to this manager.
    /// </summary>
    /// <param name="source">The source EntityManager to copy from.</param>
    internal void CopyFrom(EntityManager source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Copy packed locations (direct memory copy)
        CollectionsMarshal.SetCount(_packedLocations, source._packedLocations.Count);
        CollectionsMarshal.AsSpan(source._packedLocations).CopyTo(CollectionsMarshal.AsSpan(_packedLocations));

        // Copy allocator state
        _allocator.CopyFrom(source._allocator);

        // Copy counter
        _aliveCount = source._aliveCount;
    }
}
