namespace Paradise.BT;

/// <summary>
/// Marks the partial class that builds one tree, naming the queryable whose rows feed it.
///
/// The generator scans this class for every <see cref="INodeData"/> type it mentions, unions their
/// <see cref="ReadsAttribute{T}"/> / <see cref="WritesAttribute{T}"/>, checks the union against the
/// queryable's claims, and emits a blackboard plus a <c>Bind</c> that wires a
/// row into it.
///
/// Named <c>BehaviorTreeBinding</c> rather than <c>BehaviorTree</c> because that name is already a
/// type here. Takes a <c>Type</c> rather than a generic parameter because a queryable is a
/// <c>ref struct</c>, which cannot be a generic argument to an attribute.
///
/// Nodes are found by NAME, so how a tree composes them decides whether they are visible. A
/// builder carries its node as a generic argument on its base and is followed; a factory method
/// returns a definition and says nothing, so it must be annotated with
/// <see cref="BuildsAttribute{T}"/> or its node listed in <see cref="Also"/>.
///
/// A builder GENERATED beside this tree is the awkward case, and is handled rather than refused.
/// A generator cannot read another generator's output, so <c>new Flee(5f)</c> is an error type
/// here even though the finished compilation is fine. Such a name is recovered against a table of
/// the builders that WILL be emitted, derived from the same <c>[Builder]</c> declarations — the
/// trick Paradise.ECS uses where SystemGenerator meets QueryableGenerator's output.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class BehaviorTreeBindingAttribute : Attribute
{
    public BehaviorTreeBindingAttribute(Type queryable) => Queryable = queryable;

    /// <summary>The <c>[Queryable]</c> ref struct whose rows this tree ticks over.</summary>
    public Type Queryable { get; }

    /// <summary>
    /// Node types the tree uses but never names, because a factory builds them.
    ///
    /// The escape hatch of last resort, for somebody else's factory that carries no
    /// <see cref="BuildsAttribute{T}"/>. Prefer annotating the factory: it knows what it builds,
    /// and the answer then travels with it to every tree rather than being restated at each one.
    /// </summary>
    public Type[]? Also { get; set; }
}

/// <summary>
/// Declares which <see cref="INodeData"/> a factory method constructs.
///
/// A tree names its nodes by constructing them, which is how the binding scan finds them. A
/// FACTORY breaks that: a method returning a <c>BehaviorNodeDefinition</c> discards every trace of
/// what it built, so a tree calling it names no node at all — and if that node reads the
/// blackboard, its data is silently missing and it throws on the first tick.
///
/// The built-in factories this existed for are gone: each built-in node has a generated builder,
/// which carries its node type on its base and needs no annotation. This remains for a factory
/// somebody writes.
///
/// The factory is the thing that knows, so this is where it is written down. It survives into
/// metadata, so a tree in another assembly gets the answer without seeing the body.
///
/// <see cref="BehaviorTreeBindingAttribute.Also"/> remains the escape hatch for a factory nobody
/// has annotated.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class BuildsAttribute<T> : Attribute where T : struct;
