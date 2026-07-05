namespace Paradise.ECS;

/// <summary>
/// Codegen switch: systems generated in an assembly carrying
/// <c>[assembly: SnapshotReadSystems]</c> bind their READ-ONLY fields
/// (<c>ref readonly T</c>, <c>ReadOnlySpan&lt;T&gt;</c>, and composition data whose components
/// are all read-only) to the READ world passed to
/// <c>SystemSchedule.Run(readWorld)</c> — typically the immutable previous-tick snapshot the
/// write world was <c>CopyFrom</c>'d. Writable fields (<c>ref T</c>, <c>Span&lt;T&gt;</c>, mixed
/// composition data) bind to the write world as usual.
///
/// Reads then never alias in-flight writes, so together with <see cref="SingleWriterAttribute"/>
/// (disjoint writes) every system can execute in one fully parallel wave — build the schedule
/// with <c>SnapshotDagScheduler</c> and a parallel wave scheduler.
/// </summary>
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
