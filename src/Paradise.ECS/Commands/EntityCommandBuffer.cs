using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.ECS;

/// <summary>
/// Types of deferred commands that can be recorded in an <see cref="EntityCommandBuffer"/>.
/// </summary>
public enum CommandType
{
    /// <summary>Spawn a new entity.</summary>
    Spawn = 0,

    /// <summary>Despawn an existing entity.</summary>
    Despawn = 1,

    /// <summary>Add a component to an entity (structural change).</summary>
    AddComponent = 2,

    /// <summary>Remove a component from an entity (structural change).</summary>
    RemoveComponent = 3,

    /// <summary>Set a component value on an entity (non-structural).</summary>
    SetComponent = 4
}

/// <summary>
/// Header for a single command in the <see cref="EntityCommandBuffer"/>.
/// Packed to 16 bytes for alignment.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct CommandHeader
{
    /// <summary>The type of command.</summary>
    public byte Type;

    /// <summary>Reserved for future use.</summary>
    public byte Reserved;

    /// <summary>
    /// The component type ID. -1 for Spawn/Despawn commands.
    /// </summary>
    public short ComponentId;

    /// <summary>The entity ID.</summary>
    public int EntityId;

    /// <summary>The entity version.</summary>
    public uint EntityVersion;

    /// <summary>
    /// The size of component data following this header.
    /// 0 for Spawn/Despawn/RemoveComponent and tag components.
    /// </summary>
    public int DataSize;
}

/// <summary>
/// Records deferred entity operations (spawn, despawn, add/remove/set component)
/// for safe playback after query iteration completes. Prevents iterator corruption
/// from structural changes during iteration.
/// </summary>
/// <remarks>
/// Commands are serialized into a contiguous byte buffer and replayed sequentially.
/// Entities created via <see cref="Spawn"/> receive real, stable IDs from the
/// <see cref="EntityIdAllocator"/> immediately. These entities are not registered
/// in the <see cref="EntityManager"/> until <see cref="Playback{TMask,TConfig}"/> is called.
/// </remarks>
public sealed class EntityCommandBuffer : IDisposable
{
    private readonly ArrayBufferWriter<byte> _buffer = new();
    private readonly EntityIdAllocator _allocator;
    private readonly List<Entity> _reservedEntities = new();
    private int _commandCount;
    private bool _playedBack;
    private bool _disposed;

    /// <summary>
    /// Creates a new EntityCommandBuffer that reserves entity IDs from the specified allocator.
    /// </summary>
    /// <param name="allocator">The thread-safe entity ID allocator, typically from <see cref="EntityManager.Allocator"/>.</param>
    public EntityCommandBuffer(EntityIdAllocator allocator)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        _allocator = allocator;
    }

    /// <summary>
    /// Gets the number of recorded commands.
    /// </summary>
    public int CommandCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _commandCount;
    }

    /// <summary>
    /// Gets whether the buffer contains no commands.
    /// </summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _commandCount == 0;
    }

    /// <summary>
    /// Records a deferred entity spawn. Reserves a real entity ID from the allocator immediately.
    /// The entity is not alive until <see cref="Playback{TMask,TConfig}"/> is called.
    /// Thread-safe with respect to the allocator — multiple ECBs can call Spawn concurrently.
    /// </summary>
    /// <returns>An entity with a real, stable ID (version >= 1).</returns>
    public Entity Spawn()
    {
        ThrowIfDisposed();
        var entity = _allocator.Reserve();
        _reservedEntities.Add(entity);
        WriteCommand(CommandType.Spawn, entity.Id, entity.Version, -1, ReadOnlySpan<byte>.Empty);
        return entity;
    }

    /// <summary>
    /// Records a deferred entity despawn.
    /// </summary>
    /// <param name="entity">The entity to despawn. Can be a real or reserved entity.</param>
    public void Despawn(Entity entity)
    {
        ThrowIfDisposed();
        WriteCommand(CommandType.Despawn, entity.Id, entity.Version, -1, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Records a deferred component addition.
    /// </summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The entity to add the component to. Can be a real or reserved entity.</param>
    /// <param name="value">The component value.</param>
    public void AddComponent<T>(Entity entity, T value = default) where T : unmanaged, IComponent
    {
        ThrowIfDisposed();
        var data = T.Size > 0
            ? MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value))
            : ReadOnlySpan<byte>.Empty;
        WriteCommand(CommandType.AddComponent, entity.Id, entity.Version, T.TypeId.Value, data);
    }

    /// <summary>
    /// Records a deferred component removal.
    /// </summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The entity to remove the component from. Can be a real or reserved entity.</param>
    public void RemoveComponent<T>(Entity entity) where T : unmanaged, IComponent
    {
        ThrowIfDisposed();
        WriteCommand(CommandType.RemoveComponent, entity.Id, entity.Version, T.TypeId.Value, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Records a deferred component value set (non-structural).
    /// </summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The entity to set the component on. Can be a real or reserved entity.</param>
    /// <param name="value">The new component value.</param>
    public void SetComponent<T>(Entity entity, T value = default) where T : unmanaged, IComponent
    {
        ThrowIfDisposed();
        var data = T.Size > 0
            ? MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value))
            : ReadOnlySpan<byte>.Empty;
        WriteCommand(CommandType.SetComponent, entity.Id, entity.Version, T.TypeId.Value, data);
    }

    /// <summary>
    /// Replays all recorded commands against the specified world in order.
    /// Reserved entities are materialized (registered + placed in empty archetype) during playback.
    /// Can only be called once per recording. Call <see cref="Clear"/> to reuse.
    /// </summary>
    /// <typeparam name="TMask">The component mask type.</typeparam>
    /// <typeparam name="TConfig">The world configuration type.</typeparam>
    /// <param name="world">The world to replay commands against.</param>
    /// <exception cref="InvalidOperationException">Thrown if Playback has already been called without a Clear.</exception>
    public void Playback<TMask, TConfig>(IWorld<TMask, TConfig> world)
        where TMask : unmanaged, IBitSet<TMask>
        where TConfig : IConfig, new()
    {
        ThrowIfDisposed();

        if (_playedBack)
            throw new InvalidOperationException("Playback has already been called. Call Clear() before recording new commands.");

        _playedBack = true;

        if (_commandCount == 0)
            return;

        var span = _buffer.WrittenSpan;
        int offset = 0;
        int headerSize = Unsafe.SizeOf<CommandHeader>();

        while (offset < span.Length)
        {
            ref readonly var header = ref Unsafe.As<byte, CommandHeader>(ref Unsafe.AsRef(in span[offset]));
            var data = header.DataSize > 0
                ? span.Slice(offset + headerSize, header.DataSize)
                : ReadOnlySpan<byte>.Empty;

            int totalSize = headerSize + header.DataSize;
            // Advance past header + data + padding to 4-byte alignment
            offset += Memory.AlignUp(totalSize, 4);

            var entity = new Entity(header.EntityId, header.EntityVersion);

            switch ((CommandType)header.Type)
            {
                case CommandType.Spawn:
                    world.MaterializeEntity(entity);
                    break;
                case CommandType.Despawn:
                    world.Despawn(entity);
                    break;
                case CommandType.AddComponent:
                    world.AddComponentRaw(entity, new ComponentId(header.ComponentId), data);
                    break;
                case CommandType.RemoveComponent:
                    world.RemoveComponentRaw(entity, new ComponentId(header.ComponentId));
                    break;
                case CommandType.SetComponent:
                    world.SetComponentRaw(entity, new ComponentId(header.ComponentId), data);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown command type: {header.Type}");
            }
        }
    }

    /// <summary>
    /// Clears all recorded commands and resets the buffer for reuse.
    /// If Playback has not been called, releases reserved entity IDs back to the allocator.
    /// </summary>
    public void Clear()
    {
        ThrowIfDisposed();
        ReleaseUnplayedReservations();
        _buffer.Clear();
        _commandCount = 0;
        _reservedEntities.Clear();
        _playedBack = false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseUnplayedReservations();
        _buffer.Clear();
        _commandCount = 0;
        _reservedEntities.Clear();
    }

    private void ReleaseUnplayedReservations()
    {
        if (_playedBack || _reservedEntities.Count == 0)
            return;

        foreach (var entity in _reservedEntities)
        {
            uint nextVersion = EntityLocation.NextVersion(entity.Version);
            _allocator.Release(entity.Id, nextVersion);
        }
    }

    private void WriteCommand(CommandType type, int entityId, uint entityVersion, int componentId, ReadOnlySpan<byte> data)
    {
        int headerSize = Unsafe.SizeOf<CommandHeader>();
        int totalSize = headerSize + data.Length;
        // Pad to 4-byte alignment
        int paddedSize = Memory.AlignUp(totalSize, 4);

        var dest = _buffer.GetSpan(paddedSize);

        ref var header = ref Unsafe.As<byte, CommandHeader>(ref dest[0]);
        header.Type = (byte)type;
        header.Reserved = 0;
        header.ComponentId = (short)componentId;
        header.EntityId = entityId;
        header.EntityVersion = entityVersion;
        header.DataSize = data.Length;

        if (data.Length > 0)
            data.CopyTo(dest.Slice(headerSize));

        // Zero padding bytes
        if (paddedSize > totalSize)
            dest.Slice(totalSize, paddedSize - totalSize).Clear();

        _buffer.Advance(paddedSize);
        _commandCount++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
