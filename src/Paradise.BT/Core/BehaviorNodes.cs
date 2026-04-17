namespace Paradise.BT;

/// <summary>
/// Factory helpers for composing <see cref="BehaviorNodeDefinition"/> trees from <see cref="INodeData"/> structs.
/// </summary>
public static class BehaviorNodes
{
    public static BehaviorNodeDefinition Node<TNodeData>(TNodeData nodeData, params BehaviorNodeDefinition[] children)
        where TNodeData : struct, INodeData
        => BehaviorNodeDefinition.Create(nodeData, BehaviorNodeMetadata<TNodeData>.Metadata, children);

    private static class BehaviorNodeMetadata<TNodeData>
        where TNodeData : struct, INodeData
    {
        public static readonly BehaviorNodeMetadata Metadata = new(typeof(TNodeData).GetNodeGuid());
    }
}
