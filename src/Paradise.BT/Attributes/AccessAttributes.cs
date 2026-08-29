namespace Paradise.BT;

/// <summary>
/// This node reads <typeparamref name="T"/> from its blackboard.
///
/// Optional for a node declared alongside its tree, whose body is read directly. Required for one
/// consumed from another assembly, where no body exists — attributes reach metadata, bodies do not.
///
/// Constrained to <c>struct</c> rather than to a component type because Paradise.BT does not
/// reference Paradise.ECS. Whether a <typeparamref name="T"/> is a component (bound from the row)
/// or a plain value (supplied by the caller) is decided by the generator.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class ReadsAttribute<T> : Attribute where T : struct;

/// <summary>
/// This node writes <typeparamref name="T"/> through <see cref="IBlackboard.SetData{T}"/>.
///
/// A component is refused (PBT0008): components bind by value, so a write could not reach the
/// chunk. Write a conclusion the system applies instead.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class WritesAttribute<T> : Attribute where T : struct;

/// <summary>
/// Reads <typeparamref name="T"/> only when the entity carries it. Declared so the gap is visible
/// in the API, but refused (PBT0006): the ECS emits optional accessors on a queryable's per-entity
/// view only, never on the <c>Segments</c> view a world system iterates.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class OptionalReadsAttribute<T> : Attribute where T : struct;
