using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;
using Paradise.BT.Nodes;

namespace Paradise.BT.Sample;

/// <summary>
/// The forager's tree, and the thing being demonstrated: nothing here — and nothing in
/// Forager.cs — says what the blackboard holds. The generator takes the node types this class
/// names, reads each one's access out of its own Tick body, checks the result against
/// <see cref="ForagerRow"/>'s claims, and emits <c>ForagerTreeBlackboard</c> and
/// <c>ForagerTreeExtras</c>.
///
/// Twenty nodes — nine node types of its own plus four built-ins — and the resulting blackboard
/// carries SIX entries: three components each read by more than one node, plus Intent, Decisions,
/// and the delta time the timer needs. That gap between node count and field count is the whole
/// point — the blackboard is sized by what the tree TOUCHES, not by how big the tree is.
///
/// Nothing is listed by hand, not even the timer. <c>new Delay(0.2f)</c> is a generated builder
/// deriving from <c>DecoratorNode&lt;DelayTimerNode&gt;</c>, so the node type is on its base and the
/// scan follows it — which is why the built-ins compose with <c>new</c> while this assembly's own
/// leaves use <c>LeafNode&lt;T&gt;</c>: a builder generated BESIDE the tree is invisible to it.
/// </summary>
[BehaviorTreeBinding(typeof(ForagerRow))]
public static class ForagerTree
{
    /// <summary>
    /// <code>
    /// Selector
    /// ├─ Sequence  FLEE      ThreatNear(panic) → Flee → Delay(0.2) → Tally
    /// ├─ Sequence  FORAGE    FoodVisible → Inverter(Exhausted) → SeekFood → Tally
    /// ├─ Sequence  SETTLE    Exhausted → Rest → Noop
    /// ├─ Sequence  CONFIRM   AlreadyDecided → Tally
    /// └─ Rest                always succeeds, so the Selector always concludes
    /// </code>
    /// </summary>
    public static BehaviorTree Build() => BehaviorTreeBuilder.Build(
        new Selector(
            new Sequence(
                new LeafNode<ThreatNearNode>(new ThreatNearNode { PanicStamina = 0.1f }),
                new LeafNode<FleeNode>(new FleeNode { Distance = 5f }),
                new Delay(0.2f),
                new LeafNode<TallyNode>(new TallyNode())),
            new Sequence(
                new LeafNode<FoodVisibleNode>(new FoodVisibleNode()),
                new Inverter(
                    new LeafNode<ExhaustedNode>(new ExhaustedNode { RestBelow = 0.25f })),
                new LeafNode<SeekFoodNode>(new SeekFoodNode { ArriveWithin = 0.5f }),
                new LeafNode<TallyNode>(new TallyNode())),
            new Sequence(
                new LeafNode<ExhaustedNode>(new ExhaustedNode { RestBelow = 0.25f }),
                new LeafNode<RestNode>(new RestNode()),
                new LeafNode<NoopNode>(new NoopNode())),
            new Sequence(
                new LeafNode<AlreadyDecidedNode>(new AlreadyDecidedNode()),
                new LeafNode<TallyNode>(new TallyNode())),
            new LeafNode<RestNode>(new RestNode())));
}
