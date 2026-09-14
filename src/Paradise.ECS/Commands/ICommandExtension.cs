namespace Paradise.ECS;

/// <summary>
/// Marker for a deferred command that is not one of the closed <see cref="CommandType"/> verbs.
/// Identity is the CLR type: two packages cannot share an opcode by accident.
/// </summary>
/// <remarks>
/// Record with <see cref="EntityCommandBuffer.RecordExtension{TOp}"/>. Playback looks the compact
/// id back up to <see cref="Type"/> and calls the world's extension sink.
/// </remarks>
public interface ICommandExtension;

/// <summary>
/// Plays back <see cref="CommandType.Extension"/> commands. Worlds that do not implement this
/// reject extension playback; a sink that does not recognize <c>opType</c> should throw.
/// </summary>
public interface ICommandExtensionSink
{
    /// <summary>
    /// Applies one extension command with access to its originating buffer's staging state.
    /// </summary>
    /// <param name="buffer">The command buffer that recorded the command and owns its extension state.</param>
    /// <param name="opType">The recorded extension type (process-local id, resolved at playback).</param>
    /// <param name="entity">The remapped target entity (placeholders already resolved).</param>
    /// <param name="data">The recorded payload; empty when the op recorded no data.</param>
    void PlayExtension(EntityCommandBuffer buffer, Type opType, Entity entity, ReadOnlySpan<byte> data);
}
