namespace Paradise.BT;

/// <summary>
/// This node reads <typeparamref name="T"/> from its blackboard.
///
/// The point of declaring it: a generator can then bind the blackboard for you, and refuse a tree
/// whose queryable does not actually make <typeparamref name="T"/> reachable — rather than letting
/// the mismatch surface as a fault on the first tick.
///
/// <typeparamref name="T"/> is constrained to <c>struct</c>, not to a component type: Paradise.BT
/// does not reference Paradise.ECS and cannot name one. Whether a <typeparamref name="T"/> is a
/// component (bound from the entity) or a plain value (supplied by the caller) is decided by the
/// generator, from the type itself.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class ReadsAttribute<T> : Attribute where T : struct;

/// <summary>
/// This node writes <typeparamref name="T"/> through <see cref="IBlackboard.GetDataRef{T}"/>.
///
/// Stricter than <see cref="ReadsAttribute{T}"/>: a component claimed read-only by the queryable
/// is refused, because writing through a read-only claim is what
/// <c>[assembly: SingleWriter]</c> exists to prevent.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class WritesAttribute<T> : Attribute where T : struct;

/// <summary>
/// This node reads <typeparamref name="T"/> only when the entity carries it, testing with
/// <see cref="IBlackboard.HasData{T}"/> first.
///
/// Declared so the gap is visible in the API rather than silent, but NOT yet supported: the ECS
/// emits optional accessors on a queryable's per-entity view only, and never on the <c>Segments</c>
/// view a world system iterates. Using it is refused (PBT0005) until that changes.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class OptionalReadsAttribute<T> : Attribute where T : struct;
