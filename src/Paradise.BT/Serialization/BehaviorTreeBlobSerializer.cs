using Paradise.BLOB;

namespace Paradise.BT;

/// <summary>
/// Serializes compiled behavior trees into Paradise.BLOB payloads and restores them back.
/// </summary>
public static class BehaviorTreeBlobSerializer
{
    public static ManagedBlobAssetReference<BehaviorTreeBlob> Serialize(BehaviorNodeDefinition root)
        => Serialize(BehaviorTreeBuilder.Build(root));

    public static ManagedBlobAssetReference<BehaviorTreeBlob> Serialize(BehaviorTree tree)
        => new(CreateBlob(tree));

    public static byte[] SerializeToBytes(BehaviorNodeDefinition root)
        => SerializeToBytes(BehaviorTreeBuilder.Build(root));

    public static byte[] SerializeToBytes(BehaviorTree tree)
        => CreateBlob(tree);

    public static BehaviorTree Deserialize(byte[] blob, BehaviorTreeSerializationRegistry? registry = null)
    {
        ThrowHelper.ThrowIfNull(blob, nameof(blob));

        using var serializedTree = new ManagedBlobAssetReference<BehaviorTreeBlob>(blob);
        return Deserialize(serializedTree, registry);
    }

    public static BehaviorTree Deserialize(
        ManagedBlobAssetReference<BehaviorTreeBlob> blob,
        BehaviorTreeSerializationRegistry? registry = null)
    {
        ThrowHelper.ThrowIfNull(blob, nameof(blob));

        registry ??= BehaviorTreeSerializationRegistry.CreateBuiltIn();

        ref BehaviorTreeBlob serializedTree = ref blob.Value;
        int count = serializedTree.Nodes.Length;
        if (count == 0)
        {
            throw new InvalidOperationException("Serialized behavior tree is empty.");
        }

        var nodes = new BehaviorTreeNode[count];
        for (int i = 0; i < count; i++)
        {
            ref BehaviorTreeBlobNode serializedNode = ref serializedTree.Nodes.GetValue(i);
            nodes[i] = new BehaviorTreeNode(registry.CreateFactory(ref serializedNode), serializedTree.Nodes.GetEndIndex(i));
        }

        return new BehaviorTree(nodes);
    }

    private static byte[] CreateBlob(BehaviorTree tree)
    {
        ThrowHelper.ThrowIfNull(tree, nameof(tree));

        var builder = new StructBuilder<BehaviorTreeBlob>();
        builder.SetBuilder(ref builder.Value.Nodes, new TreeBuilder<BehaviorTreeBlobNode>(CreateTreeNode(tree, 0, out _)));
        return builder.CreateBlob();
    }

    private static StructBuilder<BehaviorTreeBlobNode> CreateNodeBuilder(BehaviorTreeNode node)
    {
        var builder = new StructBuilder<BehaviorTreeBlobNode>();
        builder.SetValue(ref builder.Value.NodeGuid, node.Factory.NodeGuid);
        builder.SetValue(ref builder.Value.NodeType, node.Factory.NodeTypeKind);

        var defaultDataBuilder = new AnyPtrBuilder();
        defaultDataBuilder.SetValue(node.Factory.CreateSerializedDefaultDataBuilder());
        builder.SetBuilder(ref builder.Value.DefaultData, defaultDataBuilder);
        return builder;
    }

    private static SerializedTreeNode CreateTreeNode(BehaviorTree tree, int index, out int nextIndex)
    {
        BehaviorTreeNode node = tree.GetCompiledNode(index);
        var children = new List<ITreeNode<BehaviorTreeBlobNode>>();
        int childIndex = index + 1;
        while (childIndex < node.EndIndex)
        {
            children.Add(CreateTreeNode(tree, childIndex, out childIndex));
        }

        nextIndex = node.EndIndex;
        return new SerializedTreeNode(CreateNodeBuilder(node), children);
    }

    private sealed class SerializedTreeNode : ITreeNode<BehaviorTreeBlobNode>
    {
        public SerializedTreeNode(IBuilder<BehaviorTreeBlobNode> valueBuilder, IReadOnlyList<ITreeNode<BehaviorTreeBlobNode>> children)
        {
            ValueBuilder = valueBuilder;
            Children = children;
        }

        public IBuilder<BehaviorTreeBlobNode> ValueBuilder { get; }

        public IReadOnlyList<ITreeNode<BehaviorTreeBlobNode>> Children { get; }
    }
}
