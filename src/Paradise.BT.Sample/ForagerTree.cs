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
/// Nothing is listed by hand, not even the timer. <c>BuiltInBehaviorNodes.Delay</c> builds a
/// <c>DelayTimerNode</c> without naming it — and it is the one built-in that reads a blackboard —
/// but the factory says what it builds with <c>[Builds&lt;T&gt;]</c>, so the scan follows it there.
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
