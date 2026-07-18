using System.Buffers;
using System.Diagnostics;
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

    /// <summary>
    /// The entity ID. May be a placeholder ID (high bit set) referring to an earlier
    /// <see cref="CommandType.Spawn"/> command in the same buffer.
    /// </summary>
    public int EntityId;

    /// <summary>The entity version (the owning buffer's ID for placeholders).</summary>
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
/// <para>
/// Commands are serialized into a contiguous byte buffer and replayed sequentially.
/// <see cref="Spawn"/> does NOT allocate a real entity ID at record time — it returns a tagged
/// placeholder handle (see <see cref="Entity.IsPlaceholder"/>). Real IDs are allocated during
/// <see cref="Playback{TMask,TConfig}"/>, in command order, so entity IDs depend only on
/// playback order — never on which thread recorded the commands. This is the foundation of
/// deterministic system-side structural changes (see <see cref="EntityCommandBufferPool"/>).
/// </para>
/// <para>
/// After playback, <see cref="Resolve"/> translates a placeholder to the real entity it became.
/// </para>
/// </remarks>
public sealed class EntityCommandBuffer : IDisposable
{
    private const int PlaceholderIndexMask = 0x7FFF_FFFF;
    private const uint PlaceholderTag = 0x8000_0000u;

    private static int s_nextBufferId;

    private readonly ArrayBufferWriter<byte> _buffer = new();

    /// <summary>Process-unique ID stamped into placeholder versions to detect (in DEBUG builds)
    /// placeholders leaking into a different buffer's commands.</summary>
    private readonly uint _bufferId = (uint)Interlocked.Increment(ref s_nextBufferId);

    /// <summary>Real entities created during playback, indexed by buffer-local spawn index.</summary>
    private readonly List<Entity> _spawnedEntities = new();

    private int _spawnCount;
    private int _commandCount;
    private bool _playedBack;
    private bool _disposed;

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
    /// Records a deferred entity spawn and returns a PLACEHOLDER handle
    /// (<see cref="Entity.IsPlaceholder"/> is true). No real entity ID is allocated until
    /// <see cref="Playback{TMask,TConfig}"/>, which allocates IDs in deterministic playback order.
    /// </summary>
    /// <remarks>
    /// RESTRICTION: the returned placeholder is valid ONLY as an argument to commands recorded
    /// on THIS buffer afterwards (<see cref="Despawn"/>, <see cref="AddComponent{T}"/>,
    /// <see cref="RemoveComponent{T}"/>, <see cref="SetComponent{T}"/>). It must NOT be stored
    /// into component data, passed to another buffer's commands, or passed to World methods —
    /// DEBUG builds throw when a foreign placeholder is detected. Use <see cref="Resolve"/>
    /// after playback to obtain the real entity.
    /// </remarks>
    /// <returns>A placeholder entity handle scoped to this buffer's current recording.</returns>
    public Entity Spawn()
    {
        ThrowIfDisposed();
        var placeholder = new Entity(unchecked((int)(PlaceholderTag | (uint)_spawnCount)), _bufferId);
        _spawnCount++;
        WriteCommand(CommandType.Spawn, placeholder.Id, placeholder.Version, -1, ReadOnlySpan<byte>.Empty);
        return placeholder;
    }

    /// <summary>
    /// Records a deferred entity despawn.
    /// </summary>
    /// <param name="entity">The entity to despawn. Can be a real entity or a placeholder from this buffer.</param>
    public void Despawn(Entity entity)
    {
        ThrowIfDisposed();
        AssertPlaceholderBelongsToThisBuffer(entity);
        WriteCommand(CommandType.Despawn, entity.Id, entity.Version, -1, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Records a deferred component addition.
    /// </summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The entity to add the component to. Can be a real entity or a placeholder from this buffer.</param>
    /// <param name="value">The component value.</param>
    public void AddComponent<T>(Entity entity, T value = default) where T : unmanaged, IComponent
    {
        ThrowIfDisposed();
        AssertPlaceholderBelongsToThisBuffer(entity);
        var data = T.Size > 0
            ? MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value))
            : ReadOnlySpan<byte>.Empty;
        WriteCommand(CommandType.AddComponent, entity.Id, entity.Version, T.TypeId.Value, data);
    }

    /// <summary>
    /// Records a deferred component removal.
    /// </summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The entity to remove the component from. Can be a real entity or a placeholder from this buffer.</param>
    public void RemoveComponent<T>(Entity entity) where T : unmanaged, IComponent
    {
        ThrowIfDisposed();
        AssertPlaceholderBelongsToThisBuffer(entity);
        WriteCommand(CommandType.RemoveComponent, entity.Id, entity.Version, T.TypeId.Value, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Records a deferred component value set (non-structural).
    /// </summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The entity to set the component on. Can be a real entity or a placeholder from this buffer.</param>
    /// <param name="value">The new component value.</param>
    public void SetComponent<T>(Entity entity, T value = default) where T : unmanaged, IComponent
    {
        ThrowIfDisposed();
        AssertPlaceholderBelongsToThisBuffer(entity);
        var data = T.Size > 0
            ? MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value))
            : ReadOnlySpan<byte>.Empty;
        WriteCommand(CommandType.SetComponent, entity.Id, entity.Version, T.TypeId.Value, data);
    }

    /// <summary>
    /// Replays all recorded commands against the specified world in order.
    /// Spawn commands allocate real entity IDs here (in command order); placeholder arguments
    /// in subsequent commands are remapped to the real entities.
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

            if ((CommandType)header.Type == CommandType.Spawn)
            {
                // Real ID allocation happens here, in command order — deterministic as long as
                // buffers are played back in a deterministic order.
                _spawnedEntities.Add(world.Spawn());
                continue;
            }

            var entity = RemapForPlayback(new Entity(header.EntityId, header.EntityVersion));

            switch ((CommandType)header.Type)
            {
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
    /// Translates a placeholder returned by <see cref="Spawn"/> into the real entity created
    /// during <see cref="Playback{TMask,TConfig}"/>. Real (non-placeholder) handles pass through
    /// unchanged. Valid after playback and until <see cref="Clear"/>.
    /// </summary>
    /// <param name="placeholder">The placeholder (or real) entity handle.</param>
    /// <returns>The real entity the placeholder became.</returns>
    /// <exception cref="InvalidOperationException">Thrown if playback has not run yet, or the
    /// placeholder does not belong to this buffer's current recording.</exception>
    public Entity Resolve(Entity placeholder)
    {
        ThrowIfDisposed();
        if (!placeholder.IsPlaceholder)
            return placeholder;
        if (!_playedBack)
            throw new InvalidOperationException("Cannot resolve a placeholder before Playback has run.");
        int index = placeholder.Id & PlaceholderIndexMask;
        if (placeholder.Version != _bufferId || index >= _spawnedEntities.Count)
            throw new InvalidOperationException(
                $"{placeholder} does not belong to this buffer's current recording.");
        return _spawnedEntities[index];
    }

    /// <summary>
    /// Clears all recorded commands and resets the buffer for reuse.
    /// Placeholders from the cleared recording become permanently unresolvable.
    /// </summary>
    public void Clear()
    {
        ThrowIfDisposed();
        _buffer.Clear();
        _commandCount = 0;
        _spawnCount = 0;
        _spawnedEntities.Clear();
        _playedBack = false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _buffer.Clear();
        _commandCount = 0;
        _spawnCount = 0;
        _spawnedEntities.Clear();
    }

    /// <summary>
    /// Remaps a recorded entity argument at playback time: placeholders resolve to the real
    /// entity created by the corresponding Spawn command; real handles pass through.
    /// </summary>
    private Entity RemapForPlayback(Entity entity)
    {
        if (!entity.IsPlaceholder)
            return entity;

        int index = entity.Id & PlaceholderIndexMask;
        if (entity.Version != _bufferId || index >= _spawnedEntities.Count)
            throw new InvalidOperationException(
                $"{entity} was recorded on a different EntityCommandBuffer (or before its Spawn command). " +
                "Placeholder handles are only valid within the buffer that created them.");
        return _spawnedEntities[index];
    }

    /// <summary>
    /// DEBUG-only guard: throws if a placeholder from a DIFFERENT buffer (or from a cleared
    /// recording) is passed to this buffer's commands. Compiled out in Release builds, where the
    /// misuse is still caught at playback by <see cref="RemapForPlayback"/> when out of range.
    /// </summary>
    [Conditional("DEBUG")]
    private void AssertPlaceholderBelongsToThisBuffer(Entity entity)
    {
        if (!entity.IsPlaceholder)
            return;
        if (entity.Version != _bufferId || (entity.Id & PlaceholderIndexMask) >= _spawnCount)
            throw new InvalidOperationException(
                $"{entity} does not belong to this EntityCommandBuffer's current recording. " +
                "Placeholder handles returned by Spawn() are only valid as arguments to commands " +
                "recorded afterwards on the SAME buffer.");
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
