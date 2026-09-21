namespace Paradise.ECS;

/// <summary>Generates systems whose read-only component access binds to the supplied read world.</summary>
/// <remarks>
/// Use <c>SnapshotDagScheduler</c> with the snapshot <c>SystemSchedule.Run</c> overload.
/// Ordinary reads observe the previous tick; writes and <c>[CurrentTick]</c> reads use the write world.
/// </remarks>
/// <remarks>
/// Without this attribute, systems get the classic single-world binding and behave identically
/// under <c>Run()</c> and <c>Run(readWorld)</c>. Semantics under snapshot reads: read-only
/// views observe LAST tick's values; intra-tick chains must flow through writable fields of the
/// same component (write-write conflicts still order waves) or through managed steps. Managed
/// pre-pass writes to the write world are visible to systems only through writable fields.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class SnapshotReadSystemsAttribute : Attribute
{
}
