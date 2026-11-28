using System.Diagnostics;
using WebGpuSharp;
using WebGpuSharp.FFI;

namespace Paradise.Runtime.Benchmarks;

/// <summary>Process-wide counters over the measured frames: every thread's allocations, collections and pauses,
/// and the WebGPU native handles still alive.</summary>
/// <remarks>WebGPUSharp releases a wrapper's native object from a finalizer unless it is disposed. A frame that
/// leaves such wrappers behind allocates few bytes, which is exactly why they pile up: gen0 collections, and
/// with them the finalizers, become rare. The handle counts show that growth; the count after a forced
/// collection shows how much of it was only waiting for one.</remarks>
internal sealed class MeasurementWindow(AllocationLedger ledger, AllocationSampler? sampler)
{
    private Counters _start;
    private Counters _end;

    public bool Ended { get; private set; }
    public int Frames { get; private set; }
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_start.Timestamp, _end.Timestamp);
    public long AllocatedBytes => _end.Allocated - _start.Allocated;
    public int Gen0 => _end.Gen0 - _start.Gen0;
    public int Gen1 => _end.Gen1 - _start.Gen1;
    public int Gen2 => _end.Gen2 - _start.Gen2;
    public TimeSpan Pause => _end.Pause - _start.Pause;
    public long WorkingSetAtStart => _start.WorkingSet;
    public long WorkingSetAtEnd => _end.WorkingSet;
    public long WorkingSetAfterCollection { get; private set; }
    public Handles HandlesAtStart => _start.Handles;
    public Handles HandlesAtEnd => _end.Handles;
    public Handles HandlesAfterCollection { get; private set; }

    public void Begin()
    {
        // Start from a clean heap, so the window's collections and handles are the run's own.
        Collect();
        _start = Counters.Read(start: true);
        ledger.Recording = true;
        if (sampler is not null) sampler.Recording = true;
    }

    public void End(int frames)
    {
        ledger.Recording = false;
        if (sampler is not null) sampler.Recording = false;
        _end = Counters.Read(start: false);
        Frames = frames;
        Collect();
        HandlesAfterCollection = Handles.Read();
        WorkingSetAfterCollection = Environment.WorkingSet;
        Ended = true;
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>Live WebGPUSharp wrappers: all of them, and the per-frame kinds.</summary>
    internal readonly record struct Handles(ulong Total, ulong TextureViews, ulong Textures)
    {
        public static Handles Read() => new(
            WebGpuSafeHandle.GetTotalActiveHandles(),
            WebGpuSafeHandle.GetActiveHandles<TextureViewHandle>(),
            WebGpuSafeHandle.GetActiveHandles<TextureHandle>());
    }

    private readonly record struct Counters(
        long Allocated, int Gen0, int Gen1, int Gen2, TimeSpan Pause, long Timestamp, Handles Handles, long WorkingSet)
    {
        /// <summary>Reads every counter, with the allocation count innermost: the working set is read from
        /// <c>/proc</c> on Linux, which allocates, so it goes before a start reading and after an end reading.</summary>
        public static Counters Read(bool start)
        {
            var workingSet = start ? Environment.WorkingSet : 0;
            var counters = new Counters(
                GC.GetTotalAllocatedBytes(precise: true), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                GC.GetTotalPauseDuration(), Stopwatch.GetTimestamp(), Handles.Read(), workingSet);
            return start ? counters : counters with { WorkingSet = Environment.WorkingSet };
        }
    }
}
