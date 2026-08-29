namespace Paradise.BT;

/// <summary>
/// Optional companion to <c>IBehaviorTreeBuilder</c> — the interface is what marks a tree type
/// and triggers the binding; this attribute exists solely to carry <see cref="Also"/>, for nodes
/// the tree never composes in a form the sweep can see.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class BehaviorTreeBindingAttribute : Attribute
{
    /// <summary>
    /// Nodes the tree uses but never names. The escape hatch of last resort, for somebody else's
    /// factory carrying no <see cref="BuildsAttribute{T}"/>; prefer annotating the factory, so the
    /// answer travels with it to every tree.
    /// </summary>
    public Type[]? Also { get; set; }
}
