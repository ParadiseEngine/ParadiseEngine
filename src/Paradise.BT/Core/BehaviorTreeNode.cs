using System.Runtime.CompilerServices;
using System.Reflection;
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

    BehaviorNodeType NodeTypeKind { get; }

    Guid NodeGuid { get; }

    IRuntimeNode CreateRuntimeNode();

    IBuilder CreateSerializedDefaultDataBuilder();
}

internal sealed class RuntimeNodeFactory<TNodeData> : IRuntimeNodeFactory
    where TNodeData : struct, INodeData
{
    private static readonly MethodInfo s_setAnyValueMethodDefinition = typeof(AnyValueBuilder)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Single(method => method.Name == nameof(AnyValueBuilder.SetValue)
            && method.IsGenericMethodDefinition
            && method.GetGenericArguments().Length == 1
            && method.GetParameters().Length == 1);

    private static readonly MethodInfo s_alignOfMethodDefinition = typeof(Utilities)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(Utilities.AlignOf)
            && method.IsGenericMethodDefinition
            && method.GetGenericArguments().Length == 1
            && method.GetParameters().Length == 0);

    private readonly TNodeData _nodeData;
    private readonly BehaviorNodeMetadata _metadata;

    public RuntimeNodeFactory(TNodeData nodeData, BehaviorNodeMetadata metadata)
    {
        _nodeData = nodeData;
        _metadata = metadata;
    }

    public int TypeId => _metadata.Id;

    public Type NodeType => typeof(TNodeData);

    public BehaviorNodeType NodeTypeKind => _metadata.Type;

    public Guid NodeGuid => _metadata.Guid;

    public IRuntimeNode CreateRuntimeNode() => new RuntimeNode<TNodeData>(_nodeData, _metadata.Id);

    public IBuilder CreateSerializedDefaultDataBuilder()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TNodeData>())
        {
            throw new NotSupportedException(
                $"Node '{typeof(TNodeData).FullName}' cannot be serialized with Paradise.BLOB because it contains managed references.");
        }

        var builder = new AnyValueBuilder();
        s_setAnyValueMethodDefinition.MakeGenericMethod(typeof(TNodeData)).Invoke(builder, [_nodeData]);
        builder.Alignment = (int)(s_alignOfMethodDefinition.MakeGenericMethod(typeof(TNodeData)).Invoke(null, null)
            ?? throw new InvalidOperationException($"Unable to determine alignment for node '{typeof(TNodeData).FullName}'."));
        return builder;
    }
}
