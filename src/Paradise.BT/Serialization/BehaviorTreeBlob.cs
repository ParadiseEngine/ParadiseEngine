using Paradise.BLOB;

namespace Paradise.BT;

/// <summary>
/// BLOB-backed serialized definition for a compiled behavior tree.
/// </summary>
public struct BehaviorTreeBlob
{
    /// <summary>
    /// Binary format version. Increment when the struct layout changes.
    /// Version 2: removed <c>BehaviorNodeType</c> field from <see cref="BehaviorTreeBlobNode"/>.
    /// </summary>
    public int FormatVersion;

    public BlobTree<BehaviorTreeBlobNode> Nodes;

    /// <summary>Current binary format version written by this library.</summary>
    public const int CurrentFormatVersion = 2;
}

/// <summary>
/// Serialized metadata and default node payload for a single behavior tree node.
/// </summary>
public struct BehaviorTreeBlobNode
{
    public Guid NodeGuid;
    public BlobPtrAny DefaultData;
}
