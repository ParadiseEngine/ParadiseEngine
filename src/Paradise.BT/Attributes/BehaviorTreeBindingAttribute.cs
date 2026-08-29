namespace Paradise.BT;

/// <summary>
/// Marks the class that builds one tree, naming the queryable whose rows feed it. The generator
/// takes the node types the class names, unions their access, checks it against the queryable's
/// claims, and emits a blackboard plus a <c>Bind</c>.
///
/// Nodes are found by NAME, so how a tree composes them decides whether they are visible:
/// <list type="bullet">
/// <item>a builder carries its node as a generic argument on its base, and is followed;</item>
/// <item>a factory RETURNING a builder keeps it in the return type, and is followed;</item>
/// <item>a factory returning a bare definition keeps nothing, and needs
/// <see cref="BuildsAttribute{T}"/> or <see cref="Also"/>;</item>
/// <item>a builder generated in THIS assembly is an error type here — a generator cannot read
/// another generator's output — and is recovered by name against the builders that will be
/// emitted.</item>
/// </list>
///
/// Named <c>BehaviorTreeBinding</c> because <c>BehaviorTree</c> is already a type. Takes a
/// <c>Type</c> rather than a generic parameter because a queryable is a <c>ref struct</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class BehaviorTreeBindingAttribute : Attribute
{
    public BehaviorTreeBindingAttribute(Type queryable) => Queryable = queryable;

    /// <summary>The <c>[Queryable]</c> ref struct whose rows this tree ticks over.</summary>
    public Type Queryable { get; }

    /// <summary>
    /// Nodes the tree uses but never names. The escape hatch of last resort, for somebody else's
    /// factory carrying no <see cref="BuildsAttribute{T}"/>; prefer annotating the factory, so the
    /// answer travels with it to every tree.
    /// </summary>
    public Type[]? Also { get; set; }
}

/// <summary>
/// Declares which <see cref="INodeData"/> a factory method constructs, for one that returns a bare
/// <c>BehaviorNodeDefinition</c> and so discards every trace of what it built. A factory returning
/// a builder needs no annotation.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class BuildsAttribute<T> : Attribute where T : struct;
