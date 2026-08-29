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
