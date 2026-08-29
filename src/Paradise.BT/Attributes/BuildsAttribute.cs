namespace Paradise.BT;

/// <summary>
/// Declares which <see cref="INodeData"/> a factory method constructs, for one that returns a bare
/// <c>BehaviorNodeDefinition</c> and so discards every trace of what it built. A factory returning
/// a builder needs no annotation; for a factory nobody has annotated, the tree can name the node
/// in <see cref="BehaviorTreeBindingAttribute.Also"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class BuildsAttribute<T> : Attribute where T : struct;
