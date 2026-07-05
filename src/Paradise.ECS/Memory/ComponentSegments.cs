using System.Runtime.CompilerServices;

namespace Paradise.ECS;

/// <summary>
/// One chunk's slice of a query, for world-system segment access. Carries the chunk's archetype
/// layout pointer (queries can span archetypes whose column offsets differ) so any component's
/// column can be resolved per access. Unmanaged so tables can live in stackalloc spans.
/// </summary>
public readonly struct ComponentSegment
{
    /// <summary>The chunk holding this segment's memory.</summary>
    public readonly ChunkHandle Chunk;

    /// <summary>The archetype layout (<see cref="ImmutableArchetypeLayout{TMask,TConfig}.DataPointer"/>).</summary>
    public readonly nint LayoutData;

    /// <summary>Number of entities in this segment.</summary>
    public readonly int Count;

    /// <summary>Cumulative flat start index of this segment.</summary>
    public readonly int Start;

    public ComponentSegment(ChunkHandle chunk, nint layoutData, int count, int start)
    {
        Chunk = chunk;
        LayoutData = layoutData;
        Count = count;
        Start = start;
    }
}

/// <summary>
/// ArraySegment-like flat view over one component column across ALL chunks matched by a query —
/// the world-system (<see cref="IWorldSystem"/>) access primitive. Indices are correlated across
/// collections built from the same segment table (index i is the same entity everywhere).
/// Stateless view: tables are rebuilt from the query each schedule run.
/// </summary>
public readonly ref struct ComponentSegments<T, TMask, TConfig>
    where T : unmanaged, IComponent
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new()
{
    private readonly ChunkManager _chunkManager;
    private readonly ReadOnlySpan<ComponentSegment> _segments;

    /// <summary>Total entity count across all segments.</summary>
    public int Length { get; }

    public ComponentSegments(ChunkManager chunkManager, ReadOnlySpan<ComponentSegment> segments)
    {
        _chunkManager = chunkManager;
        _segments = segments;
        Length = segments.Length == 0 ? 0 : segments[^1].Start + segments[^1].Count;
    }

    /// <summary>Number of contiguous segments (chunks).</summary>
    public int SegmentCount => _segments.Length;

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            int i = FindSegment(_segments, index);
            if (i >= 0)
            {
                ref readonly ComponentSegment segment = ref _segments[i];
                int local = index - segment.Start;
                int baseOffset = new ImmutableArchetypeLayout<TMask, TConfig>(segment.LayoutData).GetBaseOffset(T.TypeId);
                return ref _chunkManager.GetBytes(segment.Chunk).GetRef<T>(baseOffset + local * Unsafe.SizeOf<T>());
            }

            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    /// <summary>Binary search on cumulative Start (segments are ascending and contiguous):
    /// O(log segments) per flat access instead of a linear scan. Returns -1 when out of range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FindSegment(ReadOnlySpan<ComponentSegment> segments, int index)
    {
        int lo = 0, hi = segments.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (segments[mid].Start <= index) lo = mid;
            else hi = mid - 1;
        }

        if (segments.Length == 0) return -1;
        int local = index - segments[lo].Start;
        return (uint)local < (uint)segments[lo].Count ? lo : -1;
    }

    /// <summary>One chunk's contiguous span, for tight inner loops.</summary>
    public Span<T> Segment(int segmentIndex)
    {
        ref readonly ComponentSegment segment = ref _segments[segmentIndex];
        int baseOffset = new ImmutableArchetypeLayout<TMask, TConfig>(segment.LayoutData).GetBaseOffset(T.TypeId);
        return _chunkManager.GetBytes(segment.Chunk).GetSpan<T>(baseOffset, segment.Count);
    }

    public Enumerator GetEnumerator() => new(this);

    /// <summary>Flat by-ref enumerator across segment boundaries.</summary>
    public ref struct Enumerator
    {
        private readonly ComponentSegments<T, TMask, TConfig> _owner;
        private int _segmentIndex;
        private int _localIndex;
        private Span<T> _currentSpan;

        internal Enumerator(ComponentSegments<T, TMask, TConfig> owner)
        {
            _owner = owner;
            _segmentIndex = -1;
            _localIndex = -1;
            _currentSpan = default;
        }

        public ref T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _currentSpan[_localIndex];
        }

        public bool MoveNext()
        {
            _localIndex++;
            while (_segmentIndex < 0 || _localIndex >= _currentSpan.Length)
            {
                _segmentIndex++;
                if (_segmentIndex >= _owner.SegmentCount) return false;
                _currentSpan = _owner.Segment(_segmentIndex);
                _localIndex = 0;
            }

            return true;
        }
    }
}

/// <summary>Read-only variant of <see cref="ComponentSegments{T,TMask,TConfig}"/> — in
/// snapshot-read execution these are built over the READ world's paired chunk table.</summary>
public readonly ref struct ReadOnlyComponentSegments<T, TMask, TConfig>
    where T : unmanaged, IComponent
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new()
{
    private readonly ChunkManager _chunkManager;
    private readonly ReadOnlySpan<ComponentSegment> _segments;

    /// <summary>Total entity count across all segments.</summary>
    public int Length { get; }

    public ReadOnlyComponentSegments(ChunkManager chunkManager, ReadOnlySpan<ComponentSegment> segments)
    {
        _chunkManager = chunkManager;
        _segments = segments;
        Length = segments.Length == 0 ? 0 : segments[^1].Start + segments[^1].Count;
    }

    /// <summary>Number of contiguous segments (chunks).</summary>
    public int SegmentCount => _segments.Length;

    public ref readonly T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            int i = ComponentSegments<T, TMask, TConfig>.FindSegment(_segments, index);
            if (i >= 0)
            {
                ref readonly ComponentSegment segment = ref _segments[i];
                int local = index - segment.Start;
                int baseOffset = new ImmutableArchetypeLayout<TMask, TConfig>(segment.LayoutData).GetBaseOffset(T.TypeId);
                return ref _chunkManager.GetBytes(segment.Chunk).GetRef<T>(baseOffset + local * Unsafe.SizeOf<T>());
            }

            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    /// <summary>One chunk's contiguous span, for tight inner loops.</summary>
    public ReadOnlySpan<T> Segment(int segmentIndex)
    {
        ref readonly ComponentSegment segment = ref _segments[segmentIndex];
        int baseOffset = new ImmutableArchetypeLayout<TMask, TConfig>(segment.LayoutData).GetBaseOffset(T.TypeId);
        return _chunkManager.GetBytes(segment.Chunk).GetSpan<T>(baseOffset, segment.Count);
    }

    public Enumerator GetEnumerator() => new(this);

    /// <summary>Flat by-ref-readonly enumerator across segment boundaries.</summary>
    public ref struct Enumerator
    {
        private readonly ReadOnlyComponentSegments<T, TMask, TConfig> _owner;
        private int _segmentIndex;
        private int _localIndex;
        private ReadOnlySpan<T> _currentSpan;

        internal Enumerator(ReadOnlyComponentSegments<T, TMask, TConfig> owner)
        {
            _owner = owner;
            _segmentIndex = -1;
            _localIndex = -1;
            _currentSpan = default;
        }

        public ref readonly T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _currentSpan[_localIndex];
        }

        public bool MoveNext()
        {
            _localIndex++;
            while (_segmentIndex < 0 || _localIndex >= _currentSpan.Length)
            {
                _segmentIndex++;
                if (_segmentIndex >= _owner.SegmentCount) return false;
                _currentSpan = _owner.Segment(_segmentIndex);
                _localIndex = 0;
            }

            return true;
        }
    }
}
