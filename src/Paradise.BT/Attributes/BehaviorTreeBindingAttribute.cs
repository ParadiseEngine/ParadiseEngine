namespace Paradise.BT;

/// <summary>Adds node types that the tree binding scan cannot discover.</summary>
/// <remarks>IBehaviorTreeBuilder marks the tree; this attribute supplies only Also entries.</remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class BehaviorTreeBindingAttribute : Attribute
{
    public Type[]? Also { get; set; }
}
