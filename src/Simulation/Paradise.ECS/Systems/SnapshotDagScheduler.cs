namespace Paradise.ECS;

/// <summary>Schedules snapshot reads using write conflicts and explicit or fresh-read dependencies.</summary>
/// <remarks>
/// Use with <c>Run(readWorld)</c> and <c>[assembly: SnapshotReadSystems]</c>: ordinary reads bind
/// to the immutable previous tick, while <c>[CurrentTick]</c> reads follow same-tick writers.
/// Single-world execution requires <see cref="DefaultDagScheduler"/> because reads alias writes.
/// </remarks>
public sealed class SnapshotDagScheduler : IDagScheduler
{
    /// <inheritdoc/>
    public int[][] ComputeWaves<TMask>(ReadOnlySpan<SystemMetadata<TMask>> systems)
        where TMask : unmanaged, IBitSet<TMask>
        => DagScheduling.ComputeWaves(systems, snapshotReads: true);
}
