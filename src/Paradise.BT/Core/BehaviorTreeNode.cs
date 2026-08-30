using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.BT;

internal readonly struct BehaviorTreeNode(IRuntimeNodeFactory factory, int endIndex)
{
    public IRuntimeNodeFactory Factory { get; } = factory ?? throw new ArgumentNullException(nameof(factory));

    public int EndIndex { get; } = endIndex;
}

internal interface IRuntimeNodeFactory
{
    Type NodeType { get; }

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

internal sealed class RuntimeNodeFactory<TNodeData>(TNodeData data, BehaviorNodeMetadata metadata)
    : IRuntimeNodeFactory
    where TNodeData : struct, INode
{
    public Type NodeType => typeof(TNodeData);

    public Guid NodeGuid => metadata.Guid;

    public NodeCardinality Cardinality => metadata.Cardinality;

    public int DataSize => Unsafe.SizeOf<TNodeData>();

    public int DataAlignment => GetAlignment<TNodeData>();

    public void WriteDefaultData(Span<byte> destination)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TNodeData>())
        {
            throw new NotSupportedException(
                $"Node '{typeof(TNodeData).FullName}' cannot back an unmanaged behavior tree "
                + "instance because it contains managed references.");
        }

        // The span over the local copy must not outlive this frame — CreateReadOnlySpan takes a
        // scoped ref, so nothing stops a returned span from dangling.
        TNodeData nodeData = data;
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref nodeData, 1))
            .CopyTo(destination);
    }

    private static int GetAlignment<T>() where T : struct
        => Unsafe.SizeOf<AlignmentHelper<T>>() - Unsafe.SizeOf<T>();

    private struct AlignmentHelper<T> where T : struct
    {
        public byte Padding;
        public T Value;

        public AlignmentHelper(byte padding, T value)
        {
            Padding = padding;
            Value = value;
        }
    }
}
