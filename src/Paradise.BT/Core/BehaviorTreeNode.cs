using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.BLOB;

namespace Paradise.BT;

internal readonly struct BehaviorTreeNode
{
    public BehaviorTreeNode(IRuntimeNodeFactory factory, int endIndex)
    {
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
        EndIndex = endIndex;
    }

    public IRuntimeNodeFactory Factory { get; }

    public int EndIndex { get; }
}

internal interface IRuntimeNodeFactory
{
    int TypeId { get; }

    Type NodeType { get; }

    Guid NodeGuid { get; }

    /// <summary>How many bytes this node's data occupies — what an unmanaged instance reserves
    /// for it. See <see cref="BehaviorTreeLayout"/>.</summary>
    int DataSize { get; }

    IBuilder CreateSerializedDefaultDataBuilder();

    /// <summary>
    /// Copy this node's authored default data into <paramref name="destination"/>, which is
    /// exactly <see cref="DataSize"/> bytes.
    ///
    /// The same bytes <see cref="CreateSerializedDefaultDataBuilder"/> would serialize, and
    /// refused on the same grounds — a node holding a managed reference has no byte
    /// representation — but written straight into a caller's buffer rather than through a blob
    /// builder, because a layout is assembling one contiguous block and has nowhere to put an
    /// <c>IBuilder</c>.
    /// </summary>
    void WriteDefaultData(Span<byte> destination);
}

internal sealed class RuntimeNodeFactory<TNodeData> : IRuntimeNodeFactory
    where TNodeData : struct, INodeData
{
    private TNodeData _nodeData;
    private readonly BehaviorNodeMetadata _metadata;

    public RuntimeNodeFactory(TNodeData nodeData, BehaviorNodeMetadata metadata)
    {
        _nodeData = nodeData;
        _metadata = metadata;
    }

    public int TypeId => _metadata.Id;

    public Type NodeType => typeof(TNodeData);

    public Guid NodeGuid => _metadata.Guid;

    public int DataSize => Unsafe.SizeOf<TNodeData>();

    public void WriteDefaultData(Span<byte> destination)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TNodeData>())
        {
            throw new NotSupportedException(
                $"Node '{typeof(TNodeData).FullName}' cannot back an unmanaged behavior tree "
                + "instance because it contains managed references.");
        }

        TNodeData nodeData = _nodeData;
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref nodeData, 1))
            .CopyTo(destination);
    }

    public IBuilder CreateSerializedDefaultDataBuilder()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TNodeData>())
        {
            throw new NotSupportedException(
                $"Node '{typeof(TNodeData).FullName}' cannot be serialized with Paradise.BLOB because it contains managed references.");
        }

        TNodeData nodeData = _nodeData;
        var builder = new AnyValueBuilder();
        builder.SetBytes(
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref nodeData, 1)),
            GetAlignment<TNodeData>());
        return builder;
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
