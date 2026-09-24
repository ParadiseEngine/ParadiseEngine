namespace Paradise.ECS;

/// <summary>Adds unmanaged components through either immediate world writes or deferred commands.</summary>
/// <remarks>
/// This write-only contract lets initializers target a world or an <see cref="EntityCommandBuffer"/>
/// without requiring entity lifecycle, queries, or component references. A world applies each addition
/// immediately; a command buffer records it for playback. Recording does not guarantee that the entity
/// is alive or lacks the component at playback time. Placeholder handles belong to their originating
/// command buffer; entity handles embedded in component values are not automatically resolved.
/// </remarks>
public interface IComponentWriter
{
    /// <summary>Adds a component immediately or records its addition for playback.</summary>
    /// <typeparam name="T">The unmanaged component type.</typeparam>
    /// <param name="entity">The target entity, or a placeholder when writing to its originating buffer.</param>
    /// <param name="value">The initial component value.</param>
    void AddComponent<T>(Entity entity, T value = default) where T : unmanaged, IComponent;
}
