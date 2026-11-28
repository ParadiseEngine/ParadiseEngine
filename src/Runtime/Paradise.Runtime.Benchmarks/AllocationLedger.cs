namespace Paradise.Runtime.Benchmarks;

/// <summary>Managed bytes allocated by named phases of a frame, measured on the thread that runs each phase.</summary>
/// <remarks><see cref="GC.GetAllocatedBytesForCurrentThread"/> is exact and thread-local, so a phase on the
/// simulation thread and one on the render thread can be measured at once without either seeing the other's
/// allocations. Each phase must only ever run on one thread at a time; phases are created before the run.</remarks>
internal sealed class AllocationLedger
{
    private readonly List<Phase> _phases = [];
    private readonly Dictionary<string, Phase> _byName = new(StringComparer.Ordinal);
    private volatile bool _recording;

    public IReadOnlyList<Phase> Phases => _phases;

    /// <summary>Whether measured phases count; off during warm-up.</summary>
    public bool Recording
    {
        get => _recording;
        set => _recording = value;
    }

    /// <summary>The phase named <paramref name="name"/>, created on first use.</summary>
    /// <remarks>Call during setup: creation is not thread-safe.</remarks>
    public Phase Get(string name)
    {
        if (_byName.TryGetValue(name, out var phase)) return phase;
        phase = new Phase(name, this);
        _byName.Add(name, phase);
        _phases.Add(phase);
        return phase;
    }

    public static long Now() => GC.GetAllocatedBytesForCurrentThread();

    /// <summary>One measured part of a frame.</summary>
    internal sealed class Phase(string name, AllocationLedger ledger)
    {
        public string Name { get; } = name;
        public long Calls { get; private set; }
        public long Bytes { get; private set; }
        public long MaxBytes { get; private set; }

        /// <summary>Calls that allocated anything: a phase allocating on 1 call in 600 is a cache miss, one
        /// allocating on every call is a per-frame cost.</summary>
        public long AllocatingCalls { get; private set; }

        /// <summary>Records the bytes allocated on this thread since <paramref name="start"/> (a <see cref="Now"/>).</summary>
        public void Add(long start) => AddBytes(Now() - start);

        public void AddBytes(long bytes)
        {
            if (!ledger.Recording) return;
            Calls++;
            Bytes += bytes;
            if (bytes > 0) AllocatingCalls++;
            if (bytes > MaxBytes) MaxBytes = bytes;
        }
    }
}
