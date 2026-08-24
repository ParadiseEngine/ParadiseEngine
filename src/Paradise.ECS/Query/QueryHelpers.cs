using System.Runtime.CompilerServices;

namespace Paradise.ECS;

/// <summary>
/// Helper methods for creating query results.
/// Used by generated extension methods to minimize generated code.
/// </summary>
public static class QueryHelpers
{
    /// <summary>
    /// Creates a query result for entity-level iteration.
    /// </summary>
    /// <typeparam name="TData">The data type providing component access.</typeparam>
    /// <typeparam name="TMask">The component mask type.</typeparam>
    /// <typeparam name="TConfig">The world configuration type.</typeparam>
    /// <param name="world">The world to query.</param>
    /// <param name="description">The query description.</param>
    /// <returns>A query result for iterating over entities.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static QueryResult<TData, Archetype<TMask, TConfig>, TMask, TConfig>
        CreateQueryResult<TData, TMask, TConfig>(
            IWorld<TMask, TConfig> world,
            HashedKey<ImmutableQueryDescription<TMask>> description)
        where TData : IQueryData<TData, TMask, TConfig>, allows ref struct
        where TMask : unmanaged, IBitSet<TMask>
        where TConfig : IConfig, new()
    {
        var query = world.ArchetypeRegistry.GetOrCreateQuery(description);
        return new QueryResult<TData, Archetype<TMask, TConfig>, TMask, TConfig>(
            world.ChunkManager, world.EntityManager, query);
    }

    /// <summary>
    /// Creates a chunk query result for batch processing.
    /// </summary>
    /// <typeparam name="TChunkData">The chunk data type providing span access.</typeparam>
    /// <typeparam name="TMask">The component mask type.</typeparam>
    /// <typeparam name="TConfig">The world configuration type.</typeparam>
    /// <param name="world">The world to query.</param>
    /// <param name="description">The query description.</param>
    /// <returns>A chunk query result for iterating over chunks.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ChunkQueryResult<TChunkData, Archetype<TMask, TConfig>, TMask, TConfig>
        CreateChunkQueryResult<TChunkData, TMask, TConfig>(
            IWorld<TMask, TConfig> world,
            HashedKey<ImmutableQueryDescription<TMask>> description)
        where TChunkData : IQueryChunkData<TChunkData, TMask, TConfig>, allows ref struct
        where TMask : unmanaged, IBitSet<TMask>
        where TConfig : IConfig, new()
    {
        var query = world.ArchetypeRegistry.GetOrCreateQuery(description);
        return new ChunkQueryResult<TChunkData, Archetype<TMask, TConfig>, TMask, TConfig>(
            world.ChunkManager, world.EntityManager, query);
    }

    /// <summary>
    /// Asks a queryable's Data type whether one row belongs to it — the row-level constraints an
    /// archetype mask cannot carry (tags, today).
    /// </summary>
    /// <remarks>
    /// It exists because generated SYSTEM code cannot call
    /// <c>IQueryData.Matches</c> on a concrete Data type: the member is a static virtual with a
    /// default body, so it resolves only through a type parameter constrained to the interface.
    /// Routing the call through this generic gives every queryable an answer — the declared
    /// override where there is one, the interface's <c>true</c> everywhere else — which is what
    /// lets the system generator emit one unconditional line without knowing whether the queryable
    /// generator gave that particular queryable a filter.
    ///
    /// For an unfiltered queryable the default folds to a constant through the struct type
    /// parameter and the call disappears.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool RowMatches<TData, TMask, TConfig>(
        ChunkManager chunkManager,
        ImmutableArchetypeLayout<TMask, TConfig> layout,
        ChunkHandle chunk,
        int indexInChunk)
        where TData : IQueryData<TData, TMask, TConfig>, allows ref struct
        where TMask : unmanaged, IBitSet<TMask>
        where TConfig : IConfig, new()
        => TData.Matches(chunkManager, layout, chunk, indexInChunk);
}
