using Paradise.BT;
using Paradise.BT.Nodes.Builder;

var blackboard = new Blackboard();
blackboard.SetData(new HasTargetData { Value = true });
blackboard.SetData(new ShotsFiredData());

// Builder DSL syntax
var tree = new Selector(
    new Sequence(
        new CheckCondition(static bb => bb.GetData<HasTargetData>().Value),
        new Repeat(
            3,
            new Sequence(
                new Delay(0.5f),
                new RunAction(static bb =>
                {
                    ref ShotsFiredData shots = ref bb.GetDataRef<ShotsFiredData>();
                    shots.Value++;
                    Console.WriteLine($"Fired shot #{shots.Value}");
                    return NodeState.Success;
                })))),
    new RunAction(static _ =>
    {
        Console.WriteLine("Idling...");
        return NodeState.Success;
    })
).Build();

BehaviorTreeInstance instance = tree.CreateInstance(blackboard);

for (int i = 0; i < 10; i++)
{
    NodeState status = instance.Tick(0.25f);
    Console.WriteLine($"Tick {i + 1}: {status}");
}

public struct HasTargetData
{
    public bool Value;
}

public struct ShotsFiredData
{
    public int Value;
}
