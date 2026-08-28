using Paradise.BT;
using Paradise.BT.Nodes;
using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;

var blackboard = new Blackboard();
blackboard.SetData(new HasTargetData { Value = true });
blackboard.SetData(new ShotsFiredData());

// Builder DSL syntax. Every leaf is an unmanaged struct declared in SampleNodes.cs; the builder
// classes around them are generated from [Builder].
var tree = new Selector(
    new Sequence(
        new HasTarget(),
        new Repeat(
            3,
            new Sequence(
                new Delay(0.5f),
                new FireShot()))),
    new Idle()
).Build();

BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(blackboard);

for (int i = 0; i < 10; i++)
{
    instance.Blackboard.SetData(new BehaviorTreeTickDeltaTime(0.25f));
    NodeState status = instance.Tick();
    Console.WriteLine($"Tick {i + 1}: {status}");
}
