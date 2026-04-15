using System.Collections.ObjectModel;

namespace Paradise.BT;

/// <summary>
/// Mutable authoring representation of a behavior node and its children.
/// </summary>
public sealed class BehaviorNodeDefinition
{
    internal BehaviorNodeDefinition(IRuntimeNodeFactory factory, IEnumerable<BehaviorNodeDefinition>? children)
    {
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
        Children = new ReadOnlyCollection<BehaviorNodeDefinition>((children ?? Array.Empty<BehaviorNodeDefinition>()).ToArray());
    }

    public IReadOnlyList<BehaviorNodeDefinition> Children { get; }

    public Type NodeType => Factory.NodeType;

    internal IRuntimeNodeFactory Factory { get; }

    internal static BehaviorNodeDefinition Create<TNodeData>(TNodeData nodeData, BehaviorNodeMetadata metadata, IEnumerable<BehaviorNodeDefinition>? children)
        where TNodeData : struct, INodeData
        => new BehaviorNodeDefinition(new RuntimeNodeFactory<TNodeData>(nodeData, metadata), children);
}
