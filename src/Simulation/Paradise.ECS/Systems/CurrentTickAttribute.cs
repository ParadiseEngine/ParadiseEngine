namespace Paradise.ECS;

/// <summary>
/// Marks a read-only system field as a FRESH read: under <c>[assembly: SnapshotReadSystems]</c>
/// the field binds to the WRITE world (this tick's values, including managed pre-pass writes)
/// instead of the read world, WITHOUT claiming write access. Applicable to inline
/// <c>ref readonly T</c>, <c>EntityComponentReader&lt;T&gt;</c>, <c>ReadOnlyManagedLookup&lt;T&gt;</c>,
/// <c>TQueryable.ReadLookup</c>, and <c>TQueryable.Singleton</c> fields
/// (where it applies to all of that singleton's read-only components); any other field kind is a
/// generator error (PECS3011).
/// </summary>
/// <remarks>
/// <para>
/// Scheduling: a CurrentTick read counts as a read-vs-write conflict even under
/// <see cref="SnapshotDagScheduler"/> (which otherwise splits waves only on write-write
/// overlaps): if another system writes the component, the CurrentTick reader is ordered into a
/// LATER wave so it observes that system's same-tick write. The component flows into
/// <see cref="SystemMetadata{TMask}.FreshReadMask"/>, not the write mask, so single-writer
/// enforcement (PECS3008) is unaffected.
/// </para>
/// <para>
/// Without <c>[assembly: SnapshotReadSystems]</c>, bindings already use the write world.
/// Fresh-read metadata still orders writers before these readers.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class CurrentTickAttribute : Attribute
{
}
