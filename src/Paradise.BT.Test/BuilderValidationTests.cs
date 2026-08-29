namespace Paradise.BT.Test;

using Paradise.BT.Nodes;

/// <summary>
/// The generated builders make a miswired tree hard to write; raw
/// <see cref="BehaviorNodes.Node{T}"/> composition does not, and traversal is index math that
/// SILENTLY ignores an impossible child. Compilation is where the claim a node's
/// <c>[Builder]</c> attribute makes about child count gets checked.
/// </summary>
public sealed class BuilderValidationTests
{
    [Test]
    public async Task A_Leaf_With_A_Child_Is_Refused()
    {
        BehaviorNodeDefinition tree = BehaviorNodes.Node(
            new SuccessNode(),
            BehaviorNodes.Node(new SuccessNode()));

        await Assert.That(() => BehaviorTreeBuilder.Build(tree))
            .Throws<InvalidOperationException>()
            .WithMessageContaining(nameof(SuccessNode));
    }

    [Test]
    public async Task A_Decorator_Without_A_Child_Is_Refused()
    {
        BehaviorNodeDefinition tree = BehaviorNodes.Node(new InverterNode());

        await Assert.That(() => BehaviorTreeBuilder.Build(tree))
            .Throws<InvalidOperationException>()
            .WithMessageContaining(nameof(InverterNode));
    }

    [Test]
    public async Task A_Decorator_With_Two_Children_Is_Refused()
    {
        BehaviorNodeDefinition tree = BehaviorNodes.Node(
            new InverterNode(),
            BehaviorNodes.Node(new SuccessNode()),
            BehaviorNodes.Node(new SuccessNode()));

        await Assert.That(() => BehaviorTreeBuilder.Build(tree))
            .Throws<InvalidOperationException>()
            .WithMessageContaining(nameof(InverterNode));
    }

    /// <summary>Composites accept any count, as EntitiesBT's do — an empty sequence is legal.</summary>
    [Test]
    public async Task A_Composite_With_No_Children_Is_Allowed()
    {
        BehaviorNodeDefinition tree = BehaviorNodes.Node(new SequenceNode());

        await Assert.That(() => BehaviorTreeBuilder.Build(tree)).ThrowsNothing();
    }

    /// <summary>A node claiming nothing is checked against nothing: cardinality comes from the
    /// node's own <c>[Builder]</c> attribute, and <see cref="ProbeNode"/> carries none.</summary>
    [Test]
    public async Task A_Node_Without_A_Builder_Attribute_Is_Not_Checked()
    {
        BehaviorNodeDefinition tree = BehaviorNodes.Node(
            new ProbeNode { Result = NodeState.Success },
            BehaviorNodes.Node(new SuccessNode()));

        await Assert.That(() => BehaviorTreeBuilder.Build(tree)).ThrowsNothing();
    }
}
