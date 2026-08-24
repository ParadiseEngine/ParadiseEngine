namespace Paradise.ECS;

/// <summary>
/// Runtime information about a component type needed for layout calculation.
/// </summary>
/// <param name="Id">The component's unique identifier.</param>
/// <param name="Size">The size of the component in bytes.</param>
/// <param name="Alignment">The alignment requirement in bytes.</param>
/// <param name="ChunkAggregateSize">
/// Bytes this component reserves ONCE PER CHUNK, beyond its per-entity column. Zero for almost
/// everything.
///
/// It exists for data that summarises a chunk rather than describing an entity — a union, a
/// high-water mark, a version stamp — and the reason it is part of the LAYOUT rather than a side
/// table is that anything living inside a chunk is carried by <c>Archetype.CopyChunksFrom</c>,
/// which copies chunks byte for byte. A side table keyed by chunk handle has to be maintained by
/// hand on every copy, and gets it wrong in the one direction that matters: a missing entry reads
/// as "this chunk holds nothing", which is a wrong answer rather than a slow one.
///
/// Its consumer today is the tag system's <c>EntityTags</c>, whose per-chunk slot holds the union
/// of the tag masks in that chunk. Nothing declares this by hand — the component generator derives
/// it from whether the assembly declares any <c>[Tag]</c> types at all.
/// </param>
public readonly record struct ComponentTypeInfo(
    ComponentId Id, int Size, int Alignment, int ChunkAggregateSize = 0)
{
    /// <summary>
    /// Creates ComponentTypeInfo for a component type.
    /// </summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <returns>Type info for the component.</returns>
    public static ComponentTypeInfo Create<T>() where T : unmanaged, IComponent
    {
        return new ComponentTypeInfo(T.TypeId, T.Size, T.Alignment);
    }
}
