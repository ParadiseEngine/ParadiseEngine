namespace Paradise.BT.Test;

using Paradise.BT.Builder;
using Paradise.BT.Nodes;

/// <summary>
/// The generated builders make a miswired tree hard to write; the raw generic wrappers
/// (<see cref="LeafNode{T}"/> and friends) do not — nothing stops a leaf's data from being
/// wrapped in a decorator builder — and traversal is index math that SILENTLY ignores an
/// impossible child. Compilation is where the builder's arity gets checked against the claim
/// the node's <c>[Builder]</c> attribute makes.
/// </summary>
public sealed class BuilderValidationTests
{
    [Test]
    public async Task A_Leaf_With_A_Child_Is_Refused()
    {
        BTreeNode tree = new DecoratorNode<SuccessNode>(
            new SuccessNode(),
            new LeafNode<SuccessNode>(new SuccessNode()));

        await Assert.That(() => tree.Build())
            .Throws<InvalidOperationException>()
            .WithMessageContaining(nameof(SuccessNode));
    }

    [Test]
    public async Task A_Decorator_Without_A_Child_Is_Refused()
    {
        BTreeNode tree = new LeafNode<InverterNode>(new InverterNode());

        await Assert.That(() => tree.Build())
            .Throws<InvalidOperationException>()
            .WithMessageContaining(nameof(InverterNode));
    }

    [Test]
    public async Task A_Decorator_With_Two_Children_Is_Refused()
    {
        BTreeNode tree = new CompositeNode<InverterNode>(
            new InverterNode(),
            new LeafNode<SuccessNode>(new SuccessNode()),
            new LeafNode<SuccessNode>(new SuccessNode()));

        await Assert.That(() => tree.Build())
            .Throws<InvalidOperationException>()
            .WithMessageContaining(nameof(InverterNode));
    }

    /// <summary>Composites accept any count, as EntitiesBT's do — an empty sequence is legal.</summary>
    [Test]
    public async Task A_Composite_With_No_Children_Is_Allowed()
    {
        BTreeNode tree = new CompositeNode<SequenceNode>(new SequenceNode());

        await Assert.That(() => tree.Build()).ThrowsNothing();
    }

    /// <summary>A node claiming nothing claims Leaf: cardinality comes from the node's own
    /// <c>[Builder]</c> attribute, and <see cref="ProbeNode"/> carries none.</summary>
    [Test]
    public async Task A_Node_Without_A_Builder_Attribute_Defaults_To_Leaf()
    {
        BTreeNode tree = new DecoratorNode<ProbeNode>(
            new ProbeNode { Result = NodeState.Success },
            new LeafNode<SuccessNode>(new SuccessNode()));

        await Assert.That(() => tree.Build())
            .Throws<InvalidOperationException>()
            .WithMessageContaining(nameof(ProbeNode));
    }
}
