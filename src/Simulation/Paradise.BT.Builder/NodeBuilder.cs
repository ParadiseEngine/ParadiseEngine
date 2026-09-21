using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.BT.Builder;

internal sealed class NodeBuilder<TNodeData>(TNodeData data, BehaviorNodeMetadata metadata)
    : INodeBuilder
    where TNodeData : struct, INode
{
    public Type NodeType => typeof(TNodeData);

    public int EndIndex { get; set; }

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
