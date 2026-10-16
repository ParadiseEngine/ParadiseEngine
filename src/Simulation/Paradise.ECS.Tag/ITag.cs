namespace Paradise.ECS;

/// <summary>
/// Interface for ECS tag types.
/// Use <see cref="TagAttribute"/> on partial structs to implement this automatically.
/// </summary>
/// <remarks>
/// <para>
/// Tags are marker types that indicate entity states or categories without storing data.
/// Unlike components, tags are stored in a per-entity bitmask within chunks, enabling
/// O(1) add/remove without an archetype move. The write is still add/remove: during a
/// schedule run, <see cref="EntityCommandBufferTagExtensions.AddTag{TTag}"/> / <c>RemoveTag</c> are the
/// system path, the same as adding or removing a marker component.
/// </para>
/// <para>
/// The generated module initializer assigns IDs in fully qualified type-name order,
/// skipping explicitly assigned IDs; tags have a separate ID space from components.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Tag]
/// public partial struct IsPlayer;
///
/// [System.Runtime.InteropServices.Guid("12345678-1234-1234-1234-123456789012")]
/// [Tag]
/// public partial struct IsEnemy;
/// </code>
/// </example>
public interface ITag
{
    /// <summary>The tag type ID assigned by the generated module initializer.</summary>
    static abstract TagId TagId { get; }

    /// <summary>The stable GUID for this tag type, or <see cref="System.Guid.Empty"/> if not specified.</summary>
    /// <remarks>
    /// Unlike <see cref="TagId"/> which changes based on alphabetical ordering,
    /// this GUID provides stable identification across compilations when specified
    /// via <see cref="System.Runtime.InteropServices.GuidAttribute"/>.
    /// </remarks>
    static abstract Guid Guid { get; }
}
