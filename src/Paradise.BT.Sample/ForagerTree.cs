using Paradise.BT.Nodes;

namespace Paradise.BT.Sample;

/// <summary>
/// The forager's tree, and the thing being demonstrated: nothing here says what the blackboard
/// holds. The generator reads the node types this class names, unions the access they declare,
/// checks it against <see cref="ForagerRow"/>'s claims, and emits <c>ForagerTreeBlackboard</c> and
/// <c>ForagerTreeExtras</c>.
///
/// Twenty nodes — nine node types of its own plus four built-ins — and the resulting blackboard
/// carries SIX entries: three
/// components each read by more than one node, plus Intent, Decisions, and the delta time the
/// Also-listed timer needs. That gap between node count and field count is the whole point — the
/// blackboard is sized by what the tree TOUCHES, not by how big the tree is.
///
/// <c>DelayTimerNode</c> is listed in <c>Also</c> because <c>BuiltInBehaviorNodes.Delay</c> builds
/// it without naming it, so no scan of this file could find it — and it is the one built-in that
/// reads a blackboard.
/// </summary>
[BehaviorTreeBinding(typeof(ForagerRow), Also = new[] { typeof(DelayTimerNode) })]
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
        BuiltInBehaviorNodes.Selector(
            BuiltInBehaviorNodes.Sequence(
                BehaviorNodes.Node(new ThreatNearNode { PanicStamina = 0.1f }),
                BehaviorNodes.Node(new FleeNode { Distance = 5f }),
                BuiltInBehaviorNodes.Delay(0.2f),
                BehaviorNodes.Node(new TallyNode())),
            BuiltInBehaviorNodes.Sequence(
                BehaviorNodes.Node(new FoodVisibleNode()),
                BuiltInBehaviorNodes.Inverter(
                    BehaviorNodes.Node(new ExhaustedNode { RestBelow = 0.25f })),
                BehaviorNodes.Node(new SeekFoodNode { ArriveWithin = 0.5f }),
                BehaviorNodes.Node(new TallyNode())),
            BuiltInBehaviorNodes.Sequence(
                BehaviorNodes.Node(new ExhaustedNode { RestBelow = 0.25f }),
                BehaviorNodes.Node(new RestNode()),
                BehaviorNodes.Node(new NoopNode())),
            BuiltInBehaviorNodes.Sequence(
                BehaviorNodes.Node(new AlreadyDecidedNode()),
                BehaviorNodes.Node(new TallyNode())),
            BehaviorNodes.Node(new RestNode())));
}
