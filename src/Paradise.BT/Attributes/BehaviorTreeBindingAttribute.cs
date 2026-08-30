namespace Paradise.BT;

/// <summary>
/// Optional companion to <c>IBehaviorTreeBuilder</c> — the interface is what marks a tree type
/// and triggers the binding; this attribute exists solely to carry <see cref="Also"/>, for nodes
/// the tree never composes in a form the sweep can see.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class BehaviorTreeBindingAttribute : Attribute
{
    public Type[]? Also { get; set; }
}
