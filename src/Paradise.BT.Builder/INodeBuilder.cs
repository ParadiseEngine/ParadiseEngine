namespace Paradise.BT.Builder;

internal interface INodeBuilder
{
    Type NodeType { get; }

    /// <summary>Where this node's subtree ends, exclusive — set by the compile walk once the
    /// children are flattened.</summary>
    int EndIndex { get; set; }

    Guid NodeGuid { get; }

    /// <summary>Child-count claim from the node's <c>[Builder]</c> attribute; a node carrying
    /// none claims <see cref="NodeCardinality.Leaf"/>.</summary>
    NodeCardinality Cardinality { get; }

    /// <summary>How many bytes this node's data occupies — what an unmanaged instance reserves
    /// for it. See <see cref="BehaviorTreeLayout"/>.</summary>
    int DataSize { get; }

    /// <summary>The node struct's natural alignment — a layout packs each node's data to this,
    /// and no wider.</summary>
    int DataAlignment { get; }

    /// <summary>
    /// Copy this node's authored default data into <paramref name="destination"/>, which is
    /// exactly <see cref="DataSize"/> bytes. Refused for a node holding a managed reference —
    /// such a node has no byte representation.
    /// </summary>
    void WriteDefaultData(Span<byte> destination);
}
