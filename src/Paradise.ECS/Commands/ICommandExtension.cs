namespace Paradise.ECS;

/// <summary>
/// Marker for a deferred command that is not one of the closed <see cref="CommandType"/> verbs.
/// Identity is the CLR type: two packages cannot share an opcode by accident.
/// </summary>
/// <remarks>
/// Record with <see cref="EntityCommandBuffer.RecordExtension{TOp}"/>. Playback looks the compact
/// id back up to <see cref="Type"/> and calls <see cref="ICommandExtensionSink.PlayExtension"/>.
/// </remarks>
public interface ICommandExtension;

/// <summary>
/// Plays back <see cref="CommandType.Extension"/> commands. Worlds that do not implement this
/// reject extension playback; a sink that does not recognize <c>opType</c> should throw.
/// </summary>
public interface ICommandExtensionSink
{
    /// <summary>
    /// Applies one extension command. <paramref name="opType"/> is the <see cref="ICommandExtension"/>
    /// type that was recorded; <paramref name="data"/> is the payload written beside the header.
    /// </summary>
    /// <param name="opType">The recorded extension type (process-local id, resolved at playback).</param>
    /// <param name="entity">The remapped target entity (placeholders already resolved).</param>
    /// <param name="data">The recorded payload; empty when the op recorded no data.</param>
    void PlayExtension(Type opType, Entity entity, ReadOnlySpan<byte> data);
}
