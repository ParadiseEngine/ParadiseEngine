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
/// A node reached only through an untyped factory — <c>BuiltInBehaviorNodes.Delay(…)</c> names no
/// node type — cannot be discovered by name. List those in <see cref="Also"/>.
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
    /// <c>BuiltInBehaviorNodes.Delay(seconds)</c> is the case that forces this to exist: it
    /// returns a definition, so <c>DelayTimerNode</c> — the one built-in that reads a blackboard —
    /// appears nowhere in the tree's source. Without listing it, its
    /// <c>BehaviorTreeTickDeltaTime</c> would be missing from the blackboard and the timer would
    /// throw on its first tick.
    /// </summary>
    public Type[]? Also { get; set; }
}

/// <summary>
/// Declares which <see cref="INodeData"/> a factory method constructs.
///
/// A tree names its nodes by constructing them, which is how the binding scan finds them. A
/// FACTORY breaks that: <c>BuiltInBehaviorNodes.Delay(seconds)</c> returns a
/// <c>BehaviorNodeDefinition</c>, so the word <c>DelayTimerNode</c> appears nowhere in the tree
/// that uses it — and it is the one built-in that reads a blackboard, so missing it means the
/// timer has no delta time and throws on its first tick.
///
/// The factory is the thing that knows, so this is where it is written down. It survives into
/// metadata, so a tree in another assembly gets the answer without seeing the body.
///
/// <see cref="BehaviorTreeBindingAttribute.Also"/> remains the escape hatch for a factory nobody
/// has annotated.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class BuildsAttribute<T> : Attribute where T : struct;
