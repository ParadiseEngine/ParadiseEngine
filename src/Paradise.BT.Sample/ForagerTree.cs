using static Paradise.BT.Sample.Builder.Nodes;
using static Paradise.BT.Nodes.Builder.Nodes;
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
/// Twenty-one nodes — ten node types of its own plus four built-ins — and the resulting blackboard
/// carries SIX entries: three components each read by more than one node, plus Intent, Decisions,
/// and the delta time the timer needs. That gap between node count and field count is the whole
/// point — the blackboard is sized by what the tree TOUCHES, not by how big the tree is.
///
/// Nothing is listed by hand, and every node is composed through its generated builder — the
/// built-ins from a referenced assembly, this assembly's own leaves recovered by name, since a
/// generator cannot see another generator's output.
/// </summary>
[BehaviorTreeBinding(typeof(ForagerRow))]
public static class ForagerTree
{
    /// <summary>
    /// <code>
    /// Selector
    /// ├─ Sequence  FLEE      ThreatNear(panic) → Flee → Delay(0.2) → Tally
    /// ├─ Sequence  FORAGE    FoodVisible → Inverter(Exhausted) → ForageWorthIt → SeekFood → Tally
    /// ├─ Sequence  SETTLE    Exhausted → Rest → Noop
    /// ├─ Sequence  CONFIRM   AlreadyDecided → Tally
    /// └─ Rest                always succeeds, so the Selector always concludes
    /// </code>
    /// </summary>
    public static BehaviorTree Build() => BehaviorTreeBuilder.Build(
        Selector(
            Sequence(
                ThreatNear(0.1f),
                Flee(5f),
                Delay(0.2f),
                Tally()),
            Sequence(
                FoodVisible(),
                Inverter(
                    Exhausted(0.25f)),
                ForageWorthIt(0.3f, 6f, requireVisible: true),
                SeekFood(0.5f),
                Tally()),
            Sequence(
                Exhausted(0.25f),
                Rest(),
                Noop()),
            Sequence(
                AlreadyDecided(),
                Tally()),
            Rest()));
}
