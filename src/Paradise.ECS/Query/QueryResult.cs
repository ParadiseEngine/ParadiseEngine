using System.Runtime.CompilerServices;

namespace Paradise.ECS;

/// <summary>
/// A generic query result that iterates over entities and returns typed data instances.
/// This struct is reused across all queryable types, reducing generated code.
/// </summary>
/// <typeparam name="TData">The data type providing component access, must implement IQueryData.</typeparam>
/// <typeparam name="TArchetype">The archetype type implementing IArchetype.</typeparam>
/// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
/// <typeparam name="TConfig">The world configuration type.</typeparam>
public readonly ref struct QueryResult<TData, TArchetype, TMask, TConfig>
    where TData : IQueryData<TData, TMask, TConfig>, allows ref struct
    where TArchetype : IArchetype<TMask, TConfig>
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new()
{
    private readonly ChunkManager _chunkManager;
    private readonly IEntityManager _entityManager;
    private readonly Query<TMask, TConfig, TArchetype> _query;

    /// <summary>
    /// Creates a new query result.
    /// </summary>
    /// <param name="chunkManager">The chunk manager for memory access.</param>
    /// <param name="entityManager">The entity manager for looking up entity versions.</param>
    /// <param name="query">The underlying query.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public QueryResult(ChunkManager chunkManager, IEntityManager entityManager, Query<TMask, TConfig, TArchetype> query)
    {
        _chunkManager = chunkManager;
        _entityManager = entityManager;
        _query = query;
    }

    /// <summary>
    /// Gets the total number of entities matching this query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// How many entities live in the archetypes this query matches — an UPPER BOUND on what
    /// iteration will yield, and exactly equal to it when <typeparamref name="TData"/> applies no
    /// row filter (which is almost every queryable). A queryable declaring <c>[WithTag&lt;T&gt;]</c>
    /// yields a subset, because whether a row carries a tag is not something an archetype knows.
    /// </para>
    /// <para>
    /// It is a bound rather than a count so that it can be a PROPERTY. Answering exactly means
    /// walking the rows, and a property that sometimes iterates — depending on a queryable's
    /// declaration, invisibly at the call site — is the kind of cost that gets discovered in a
    /// profiler rather than in review. <see cref="Count"/> is that walk, spelled as a method
    /// because it is one.
    /// </para>
    /// </remarks>
    public int EntityCapacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _query.EntityCount;
    }

    /// <summary>
    /// Counts what this query will actually yield, filters included.
    /// </summary>
    /// <remarks>
    /// A method, not a property, because it iterates: O(1) archetype bookkeeping cannot answer it
    /// for a filtered queryable. Prefer <see cref="EntityCapacity"/> when a bound will do, and
    /// <see cref="IsEmpty"/> when the question is merely whether anything matches.
    /// </remarks>
    /// <returns>The number of rows iteration would produce.</returns>
    public int Count()
    {
        if (!TData.IsFiltered) return _query.EntityCount;
        var count = 0;
        var enumerator = GetEnumerator();
        while (enumerator.MoveNext()) count++;
        return count;
    }

    /// <summary>
    /// Gets whether this query has any matching entities.
    /// </summary>
    /// <remarks>
    /// Cheap in both directions, which is why it stays a property while <see cref="Count"/> did
    /// not. Unfiltered it is archetype bookkeeping. Filtered it stops at the FIRST match, and the
    /// empty case — the one that has to look everywhere — skips whole chunks whose summary rules
    /// them out, so "nothing carries this tag" costs chunks rather than entities.
    /// </remarks>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (!TData.IsFiltered) return _query.IsEmpty;
            var enumerator = GetEnumerator();
            return !enumerator.MoveNext();
        }
    }

    /// <summary>
    /// Returns an enumerator that iterates through all entities in the matching archetypes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator GetEnumerator() => new(_chunkManager, _entityManager, _query);

    /// <summary>
    /// Enumerator for iterating over TData instances.
    /// </summary>
    public ref struct Enumerator
    {
        private readonly ChunkManager _chunkManager;
        private readonly IEntityManager _entityManager;
        private Query<TMask, TConfig, TArchetype>.ChunkEnumerator _chunkEnumerator;
        private ImmutableArchetypeLayout<TMask, TConfig> _currentLayout;
        private ChunkHandle _currentChunk;
        private int _indexInChunk;
        private int _entitiesInChunk;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Enumerator(ChunkManager chunkManager, IEntityManager entityManager, Query<TMask, TConfig, TArchetype> query)
        {
            _chunkManager = chunkManager;
            _entityManager = entityManager;
            _chunkEnumerator = query.Chunks.GetEnumerator();
            _currentLayout = default;
            _currentChunk = default;
            _indexInChunk = -1;
            _entitiesInChunk = 0;
        }

        /// <summary>
        /// Gets the current data instance.
        /// </summary>
        public TData Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => TData.Create(_chunkManager, _entityManager, _currentLayout, _currentChunk, _indexInChunk);
        }

        /// <summary>
        /// Advances to the next entity.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            _indexInChunk++;
            while (true)
            {
                while (_indexInChunk >= _entitiesInChunk)
                {
                    if (!_chunkEnumerator.MoveNext()) return false;
                    var info = _chunkEnumerator.Current;
                    // Coarse pass: a chunk that provably holds no match is stepped over without
                    // reading a single row. Conservative by contract — see IQueryData.ChunkMatches.
                    if (!TData.ChunkMatches(_chunkManager, info.Archetype.Layout, info.Handle))
                    {
                        continue;
                    }
                    _currentLayout = info.Archetype.Layout;
                    _currentChunk = info.Handle;
                    _entitiesInChunk = info.EntityCount;
                    _indexInChunk = 0;
                }

                // Row-level constraints the archetype mask cannot carry — tags, today. The default
                // is a literal true reached through a struct type parameter, so for every queryable
                // that declares none this compiles away and the loop is the one it replaced.
                if (TData.Matches(_chunkManager, _currentLayout, _currentChunk, _indexInChunk))
                {
                    return true;
                }
                _indexInChunk++;
            }
        }
    }
}

/// <summary>
/// A generic chunk query result that iterates over chunks and returns typed chunk data instances.
/// This struct is reused across all queryable types, reducing generated code.
///
/// <para><b>Row filters do not apply here.</b> This yields CHUNKS, and a chunk-level filter is a
/// different question from a row-level one — a chunk holds matching and non-matching entities
/// alike. A queryable declaring <c>[WithTag&lt;T&gt;]</c> or <c>[WithoutTag&lt;T&gt;]</c> therefore
/// still hands out whole chunks
/// through this path, and a caller batching over the spans must test the rows itself. See
/// ParadiseEngine#166.</para>
/// </summary>
/// <typeparam name="TChunkData">The chunk data type providing span access, must implement IQueryChunkData.</typeparam>
/// <typeparam name="TArchetype">The archetype type implementing IArchetype.</typeparam>
/// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
/// <typeparam name="TConfig">The world configuration type.</typeparam>
public readonly ref struct ChunkQueryResult<TChunkData, TArchetype, TMask, TConfig>
    where TChunkData : IQueryChunkData<TChunkData, TMask, TConfig>, allows ref struct
    where TArchetype : IArchetype<TMask, TConfig>
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new()
{
    private readonly ChunkManager _chunkManager;
    private readonly IEntityManager _entityManager;
    private readonly Query<TMask, TConfig, TArchetype> _query;

    /// <summary>
    /// Creates a new chunk query result.
    /// </summary>
    /// <param name="chunkManager">The chunk manager for memory access.</param>
    /// <param name="entityManager">The entity manager for looking up entity versions.</param>
    /// <param name="query">The underlying query.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ChunkQueryResult(ChunkManager chunkManager, IEntityManager entityManager, Query<TMask, TConfig, TArchetype> query)
    {
        _chunkManager = chunkManager;
        _entityManager = entityManager;
        _query = query;
    }

    /// <summary>
    /// Gets the total number of entities matching this query.
    /// </summary>
    public int EntityCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _query.EntityCount;
    }

    /// <summary>
    /// Gets whether this query has any matching entities.
    /// </summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _query.IsEmpty;
    }

    /// <summary>
    /// Returns an enumerator that iterates through all chunks in the matching archetypes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator GetEnumerator() => new(_chunkManager, _entityManager, _query);

    /// <summary>
    /// Enumerator for iterating over TChunkData instances.
    /// </summary>
    public ref struct Enumerator
    {
        private readonly ChunkManager _chunkManager;
        private readonly IEntityManager _entityManager;
        private Query<TMask, TConfig, TArchetype>.ChunkEnumerator _chunkEnumerator;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Enumerator(ChunkManager chunkManager, IEntityManager entityManager, Query<TMask, TConfig, TArchetype> query)
        {
            _chunkManager = chunkManager;
            _entityManager = entityManager;
            _chunkEnumerator = query.Chunks.GetEnumerator();
        }

        /// <summary>
        /// Gets the current chunk data instance.
        /// </summary>
        public TChunkData Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                var info = _chunkEnumerator.Current;
                return TChunkData.Create(_chunkManager, _entityManager, info.Archetype.Layout, info.Handle, info.EntityCount);
            }
        }

        /// <summary>
        /// Advances to the next chunk.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => _chunkEnumerator.MoveNext();
    }
}
