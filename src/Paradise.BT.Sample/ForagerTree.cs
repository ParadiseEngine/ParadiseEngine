using static Paradise.BT.Sample.Builder.Nodes;
using static Paradise.BT.Nodes.Builder.Nodes;
using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;
using Paradise.BT.Nodes;

namespace Paradise.BT.Sample;

/// <summary>Demonstrates a 21-node tree whose combined access generates six blackboard entries.</summary>
public readonly struct ForagerTree : IBehaviorTreeBuilder
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
    public static BTreeNode Build() =>
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
                ForageWorthIt(minStamina: 0.3f, maxDistance: 6f, requireVisible: true),
                SeekFood(0.5f),
                Tally()),
            Sequence(
                Exhausted(0.25f),
                Rest(),
                Noop()),
            Sequence(
                AlreadyDecided(),
                Tally()),
            Rest());
}
