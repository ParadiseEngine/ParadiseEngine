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

    IRuntimeNode CreateRuntimeNode();

    IBuilder CreateSerializedDefaultDataBuilder();
}

internal sealed class RuntimeNodeFactory<TNodeData> : IRuntimeNodeFactory
    where TNodeData : struct, INodeData
{
    private readonly TNodeData _nodeData;
    private readonly BehaviorNodeMetadata _metadata;

    public RuntimeNodeFactory(TNodeData nodeData, BehaviorNodeMetadata metadata)
    {
        _nodeData = nodeData;
        _metadata = metadata;
    }

    public int TypeId => _metadata.Id;

    public Type NodeType => typeof(TNodeData);

    public Guid NodeGuid => _metadata.Guid;

    public IRuntimeNode CreateRuntimeNode() => new RuntimeNode<TNodeData>(_nodeData, _metadata.Id);

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
