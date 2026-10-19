using System.Collections.Immutable;

namespace Paradise.ECS;

/// <summary>
/// Manages unique archetypes and provides lookup by component mask.
/// Uses shared metadata for archetype IDs, layouts, and graph edges.
/// Structural changes require owner-thread access; lazy query-cache resolution is synchronized for scheduler workers.
/// </summary>
/// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
/// <typeparam name="TConfig">The world configuration type.</typeparam>
public sealed class ArchetypeRegistry<TMask, TConfig> : IArchetypeRegistry<TMask, TConfig, Archetype<TMask, TConfig>>
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new()
{
    private readonly SharedArchetypeMetadata<TMask, TConfig> _sharedMetadata;
    private readonly ChunkManager _chunkManager;

    private readonly List<Archetype<TMask, TConfig>?> _archetypes = new();
    private readonly List<List<Archetype<TMask, TConfig>>?> _queryCache = new();

    // Guards _queryCache. Lazy query resolution can run on parallel scheduler threads at
    // dispatch time (singleton queryables, world systems); the List grows unsynchronized
    // otherwise. Nested lock order is registry -> shared metadata only, never the reverse.
    private readonly object _queryCacheLock = new();

    /// <summary>
    /// Temporary list for collecting matched query IDs during archetype operations.
    /// Reused to avoid allocations on each operation.
    /// </summary>
    private readonly List<int> _tempMatchedQueries = new();

    /// <summary>Creates a new archetype registry using the specified shared metadata.</summary>
    /// <param name="sharedMetadata">The shared metadata to use.</param>
    /// <param name="chunkManager">The chunk manager for memory allocation.</param>
    public ArchetypeRegistry(SharedArchetypeMetadata<TMask, TConfig> sharedMetadata, ChunkManager chunkManager)
    {
        ArgumentNullException.ThrowIfNull(sharedMetadata);
        ArgumentNullException.ThrowIfNull(chunkManager);
        _sharedMetadata = sharedMetadata;
        _chunkManager = chunkManager;
    }

    /// <summary>Gets all archetypes in this registry.</summary>
    internal IReadOnlyList<Archetype<TMask, TConfig>?> Archetypes => _archetypes;

    /// <summary>The shared metadata used by this registry.</summary>
    internal SharedArchetypeMetadata<TMask, TConfig> SharedMetadata => _sharedMetadata;

    /// <summary>The component metadata defining this registry's layouts.</summary>
    public ImmutableArray<ComponentTypeInfo> TypeInfos => _sharedMetadata.TypeInfos;

    /// <summary>Gets or creates an archetype for the given component mask.</summary>
    /// <param name="mask">The component mask defining the archetype.</param>
    /// <returns>The archetype store for this mask.</returns>
    public Archetype<TMask, TConfig> GetOrCreate(HashedKey<TMask> mask)
    {
        var matchedQueries = _tempMatchedQueries;
        matchedQueries.Clear();

        int archetypeId = _sharedMetadata.GetOrCreateArchetypeId(mask, matchedQueries);

        return GetOrCreateById(archetypeId, matchedQueries);
    }

    /// <summary>
    /// Gets or creates the archetype resulting from adding a component to the source archetype.
    /// Caches the target archetype in graph edges; collecting matching query IDs still scans the queries.
    /// </summary>
    /// <param name="source">The source archetype.</param>
    /// <param name="componentId">The component to add.</param>
    /// <returns>The target archetype with the component added.</returns>
    public Archetype<TMask, TConfig> GetOrCreateWithAdd(
        Archetype<TMask, TConfig> source,
        ComponentId componentId)
    {
        ArgumentNullException.ThrowIfNull(source);

        var matchedQueries = _tempMatchedQueries;
        matchedQueries.Clear();

        int targetId = _sharedMetadata.GetOrCreateArchetypeIdWithAdd(source.Id, componentId, matchedQueries);

        return GetOrCreateById(targetId, matchedQueries);
    }

    /// <summary>
    /// Gets or creates the archetype resulting from removing a component from the source archetype.
    /// Caches the target archetype in graph edges; collecting matching query IDs still scans the queries.
    /// </summary>
    /// <param name="source">The source archetype.</param>
    /// <param name="componentId">The component to remove.</param>
    /// <returns>The target archetype with the component removed.</returns>
    public Archetype<TMask, TConfig> GetOrCreateWithRemove(
        Archetype<TMask, TConfig> source,
        ComponentId componentId)
    {
        ArgumentNullException.ThrowIfNull(source);

        var matchedQueries = _tempMatchedQueries;
        matchedQueries.Clear();

        int targetId = _sharedMetadata.GetOrCreateArchetypeIdWithRemove(source.Id, componentId, matchedQueries);

        return GetOrCreateById(targetId, matchedQueries);
    }

    /// <summary>
    /// Gets or creates a query for the given description.
    /// Queries are cached and reused for the same description.
    /// </summary>
    /// <param name="description">The query description defining matching criteria.</param>
    /// <returns>The query for this description.</returns>
    public Query<TMask, TConfig, Archetype<TMask, TConfig>> GetOrCreateQuery(HashedKey<ImmutableQueryDescription<TMask>> description)
    {
        lock (_queryCacheLock)
        {
            int queryId = _sharedMetadata.GetOrCreateQueryId(description);

            if ((uint)queryId < (uint)_queryCache.Count && _queryCache[queryId] is { } existingList)
            {
                return new Query<TMask, TConfig, Archetype<TMask, TConfig>>(existingList);
            }

            int requiredCount = queryId + 1;
            _queryCache.EnsureCapacity(requiredCount);
            for (int i = _queryCache.Count; i < requiredCount; i++)
            {
                _queryCache.Add(null);
            }

            var matchedIds = _sharedMetadata.GetMatchedArchetypeIds(queryId);
            var archetypes = new List<Archetype<TMask, TConfig>>(matchedIds.Count);
            // New archetypes will be added via NotifyQueries when created
            AddExistingArchetypes(matchedIds, archetypes);

            _queryCache[queryId] = archetypes;
            return new Query<TMask, TConfig, Archetype<TMask, TConfig>>(archetypes);
        }
    }

    /// <summary>An archetype by its ID.</summary>
    /// <param name="archetypeId">The archetype ID.</param>
    /// <returns>The archetype store, or null if not found in this world.</returns>
    public Archetype<TMask, TConfig>? GetById(int archetypeId)
    {
        return (uint)archetypeId < (uint)_archetypes.Count ? _archetypes[archetypeId] : null;
    }

    /// <summary>Clears all archetypes and query caches from this registry.</summary>
    public void Clear()
    {
        for (int i = 0; i < _archetypes.Count; i++)
        {
            _archetypes[i]?.Clear();
        }
        _archetypes.Clear();
        lock (_queryCacheLock)
        {
            _queryCache.Clear();
        }
        _tempMatchedQueries.Clear();
    }

    /// <summary>
    /// Overwrites this registry's archetypes (chunks and entity counts) with copies of the source registry's.
    /// Both registries must share the same SharedArchetypeMetadata.
    /// </summary>
    /// <param name="source">The source registry to copy from.</param>
    internal void CopyFrom(ArchetypeRegistry<TMask, TConfig> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        CopyFrom(source, source._chunkManager);
    }

    /// <summary>
    /// Overwrites this registry's archetypes with copies of the source registry's, reading source chunks
    /// through the specified chunk manager. Both registries must share the same SharedArchetypeMetadata.
    /// </summary>
    /// <remarks>
    /// Archetype IDs are global to the shared metadata, so an archetype this registry already holds is
    /// overwritten in place, keeping its chunk list and the cached queries that list it; only archetypes
    /// new to this registry are created. A registry holding an archetype the source lacks is cleared
    /// first, so the copy never shows an archetype the source does not have.
    /// </remarks>
    /// <param name="source">The source registry to copy from.</param>
    /// <param name="sourceChunkManager">The chunk manager to use for reading source chunk data.</param>
    internal void CopyFrom(ArchetypeRegistry<TMask, TConfig> source, ChunkManager sourceChunkManager)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceChunkManager);

        if (HoldsArchetypeMissingFrom(source))
            Clear();

        bool created = false;
        for (int i = 0; i < source._archetypes.Count; i++)
        {
            var sourceArchetype = source._archetypes[i];
            if (sourceArchetype == null)
                continue;

            var targetArchetype = GetById(i);
            if (targetArchetype == null)
            {
                // TODO: Use HashedKey<TMask> as type of ComponentMask to avoid cast (#44)
                targetArchetype = GetOrCreate((HashedKey<TMask>)sourceArchetype.Layout.ComponentMask);
                created = true;
            }

            targetArchetype.CopyChunksFrom(sourceArchetype, sourceChunkManager);
        }

        // Creation appends to cached query lists. Restore the order a fresh registry builds, so iterating
        // a pooled copy never depends on which earlier copy created its archetypes.
        if (created)
            RebuildQueryCaches();
    }

    private bool HoldsArchetypeMissingFrom(ArchetypeRegistry<TMask, TConfig> source)
    {
        for (int i = 0; i < _archetypes.Count; i++)
        {
            if (_archetypes[i] != null && source.GetById(i) == null)
                return true;
        }
        return false;
    }

    /// <summary>Refills every cached query list in the shared metadata's match order, keeping the list instances.</summary>
    private void RebuildQueryCaches()
    {
        lock (_queryCacheLock)
        {
            for (int queryId = 0; queryId < _queryCache.Count; queryId++)
            {
                if (_queryCache[queryId] is not { } archetypes)
                    continue;
                archetypes.Clear();
                AddExistingArchetypes(_sharedMetadata.GetMatchedArchetypeIds(queryId), archetypes);
            }
        }
    }

    /// <summary>Adds the archetypes of this world among <paramref name="matchedIds"/>, in their order.</summary>
    private void AddExistingArchetypes(IReadOnlyList<int> matchedIds, List<Archetype<TMask, TConfig>> archetypes)
    {
        int matchedCount = matchedIds.Count;
        int localArchetypeCount = _archetypes.Count;
        for (int i = 0; i < matchedCount; i++)
        {
            int archetypeId = matchedIds[i];
            if ((uint)archetypeId < (uint)localArchetypeCount && _archetypes[archetypeId] is { } archetype)
                archetypes.Add(archetype);
        }
    }

    /// <summary>Gets or creates an archetype instance by its global ID.</summary>
    /// <param name="archetypeId">The global archetype ID.</param>
    /// <param name="matchedQueries">The list of matching query IDs from shared metadata.</param>
    /// <returns>The archetype instance for this world.</returns>
    private Archetype<TMask, TConfig> GetOrCreateById(int archetypeId, List<int> matchedQueries)
    {
        if ((uint)archetypeId < (uint)_archetypes.Count && _archetypes[archetypeId] is { } existing)
        {
            return existing;
        }

        int requiredCount = archetypeId + 1;
        _archetypes.EnsureCapacity(requiredCount);
        for (int i = _archetypes.Count; i < requiredCount; i++)
        {
            _archetypes.Add(null);
        }

        var layoutData = _sharedMetadata.GetLayoutData(archetypeId);
        var archetype = new Archetype<TMask, TConfig>(archetypeId, layoutData, _chunkManager, _sharedMetadata.TypeInfos);

        _archetypes[archetypeId] = archetype;

        NotifyQueries(archetype, matchedQueries);
        matchedQueries.Clear();
        return archetype;
    }

    /// <summary>
    /// Notifies matching queries about a newly created archetype.
    /// Uses pre-computed matched query IDs for efficient iteration.
    /// </summary>
    /// <param name="archetype">The newly created archetype.</param>
    /// <param name="matchedQueries">The list of matching query IDs from shared metadata.</param>
    private void NotifyQueries(Archetype<TMask, TConfig> archetype, List<int> matchedQueries)
    {
        lock (_queryCacheLock)
        {
            int localQueryCount = _queryCache.Count;
            int matchedCount = matchedQueries.Count;

            for (int i = 0; i < matchedCount; i++)
            {
                int queryId = matchedQueries[i];
                if ((uint)queryId < (uint)localQueryCount && _queryCache[queryId] is { } archetypes)
                {
                    archetypes.Add(archetype);
                }
            }
        }
    }
}
