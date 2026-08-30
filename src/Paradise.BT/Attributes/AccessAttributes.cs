namespace Paradise.BT;

/// <summary>
/// This node reads <typeparamref name="T"/> from its blackboard.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class ReadsAttribute<T> : Attribute where T : struct;

/// <summary>
/// This node writes <typeparamref name="T"/> through <see cref="IBlackboard.SetData{T}"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class WritesAttribute<T> : Attribute where T : struct;
