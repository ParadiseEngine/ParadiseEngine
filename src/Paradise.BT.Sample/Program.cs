using Paradise.BT;

var blackboard = new Blackboard();
blackboard.SetData(new HasTargetData { Value = true });
blackboard.SetData(new ShotsFiredData());

var tree = BehaviorTreeBuilder.Build(
    BehaviorNodes.Selector(
        BehaviorNodes.Sequence(
            BehaviorNodes.Condition(static bb => bb.GetData<HasTargetData>().Value),
            BehaviorNodes.Repeat(
                3,
                BehaviorNodes.Sequence(
                    BehaviorNodes.Delay(0.5f),
                    BehaviorNodes.Action(static bb =>
                    {
                        ref ShotsFiredData shots = ref bb.GetDataRef<ShotsFiredData>();
                        shots.Value++;
                        Console.WriteLine($"Fired shot #{shots.Value}");
                        return NodeState.Success;
                    })))),
        BehaviorNodes.Action(static _ =>
        {
            Console.WriteLine("Idling...");
            return NodeState.Success;
        })));

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
