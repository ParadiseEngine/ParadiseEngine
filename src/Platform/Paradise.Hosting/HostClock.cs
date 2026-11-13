using System.Diagnostics;

namespace Paradise.Hosting;

/// <summary>Monotonic time shared by one host run's simulation and presentation.</summary>
/// <remarks>Both loops read the same clock, so a snapshot stamped by the simulation and a frame time
/// passed to presentation are on one timebase: a presenter can interpolate between two snapshots
/// at <c>Render</c>'s elapsed time without inventing its own. Thread-safe.</remarks>
public sealed class HostClock
{
    private readonly long _start = Stopwatch.GetTimestamp();

    /// <summary>Time since the host started the run.</summary>
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_start);
}
