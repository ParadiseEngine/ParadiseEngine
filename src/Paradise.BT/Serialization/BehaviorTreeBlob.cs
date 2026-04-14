using Paradise.BLOB;

namespace Paradise.BT;

/// <summary>
/// BLOB-backed serialized definition for a compiled behavior tree.
/// </summary>
public struct BehaviorTreeBlob
{
    public BlobTree<BehaviorTreeBlobNode> Nodes;
}

/// <summary>
/// Serialized metadata and default node payload for a single behavior tree node.
/// </summary>
public struct BehaviorTreeBlobNode
{
    public Guid NodeGuid;
    public BehaviorNodeType NodeType;
    public BlobPtrAny DefaultData;
}
