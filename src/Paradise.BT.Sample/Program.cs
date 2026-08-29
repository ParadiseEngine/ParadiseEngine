using Paradise.BT;
using Paradise.BT.Nodes;
using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;
using Paradise.BT.Sample;

// ---------------------------------------------------------------------------------------------
// 1. The builder DSL, over a hand-written blackboard.
// ---------------------------------------------------------------------------------------------

var blackboard = new Blackboard();
blackboard.SetData(new HasTargetData { Value = true });
blackboard.SetData(new ShotsFiredData());

// Every leaf is an unmanaged struct declared in SampleNodes.cs; the builder classes around them
// are generated from [Builder].
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

// ---------------------------------------------------------------------------------------------
// 2. The GENERATED blackboard, over an ECS row.
//
// Nothing below names a blackboard type that anyone wrote. ForagerTreeBlackboard was emitted from
// what the tree's node types declare with [Reads<T>] / [Writes<T>], checked against ForagerRow's
// claims. It holds a ref to each value, so a write lands in the local passed to Bind.
//
// Running it under PublishAot is the part worth having: generated code plus trimming plus native
// compilation is where this would break first if it were going to.
// ---------------------------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("Forager — the generated blackboard, over a row:");

BehaviorTree forager = ForagerTree.Build();
BehaviorTreeLayout layout = BehaviorTreeLayout.Build(forager);

// The instance owns the two per-agent buffers; the blackboard is bound per tick, because a
// generated blackboard is a ref struct no field can hold.
BehaviorTreeInstance foragerInstance = layout.CreateInstance();

Console.WriteLine($"  {layout.NodeCount} nodes, {layout.RuntimeDataSize} bytes of node data.");

// One forager, walking a line. In a game these three come off a chunk; here they are locals,
// because the generated Bind takes components rather than a query.
var position = new Position { X = 0f };
var stamina = new Stamina { Value = 1f };

// Four situations, so every branch of the Selector is taken at least once.
(string Label, Senses Senses, float Stamina)[] situations =
[
    ("threatened", new Senses { ThreatNear = true, FoodX = 2f, FoodVisible = true }, 0.9f),
    ("food ahead", new Senses { FoodVisible = true, FoodX = 4f }, 0.8f),
    ("worn out", new Senses { FoodVisible = true, FoodX = 4f }, 0.1f),
    ("nothing doing", new Senses(), 0.5f),
];

var deltaTime = new BehaviorTreeTickDeltaTime(0.5f);

foreach ((string label, Senses senses, float energy) in situations)
{
    stamina = stamina with { Value = energy };

    // The tree writes straight into these: the blackboard holds a ref to each.
    var intent = default(Intent);
    var decisions = default(Decisions);
    var bb = ForagerTreeBlackboard.Bind(
        behaviorTreeTickDeltaTime: in deltaTime,
        decisions: ref decisions,
        intent: ref intent,
        position: in position,
        senses: in senses,
        stamina: in stamina);

    NodeState state = foragerInstance.Tick(bb);

    Console.WriteLine(
        $"  {label,-14} stamina {energy:0.0} -> {state,-7} {intent.Kind} "
        + (intent.HasGoal ? $"goal x={intent.GoalX:0.0}" : "no goal")
        + $"  (decisions so far: {decisions.Count})");
}

// ---------------------------------------------------------------------------------------------
// 3. How a tree CHANGES anything, given that components are read-only to it.
//
// A node COULD write Position now — the blackboard holds a ref to it — but only if ForagerRow
// claimed it writable, which it does not. What these nodes write is a CONCLUSION, Intent, which
// the caller applies. In the game that caller is EnemySystem, turning the goal into a steering
// intent; here it is this loop, walking the forager toward whatever the tree decided.
//
// That round trip is the point: read, conclude, apply, read again — and it is why the same tree
// can drive a body steered any way you like.
// ---------------------------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("Walking toward what the tree decides:");

position = new Position { X = 0f };
stamina = new Stamina { Value = 0.9f };
var world = new Senses { FoodVisible = true, FoodX = 4f };

for (int step = 1; step <= 5; step++)
{
    var intent = default(Intent);
    var decisions = default(Decisions);
    var bb = ForagerTreeBlackboard.Bind(
        behaviorTreeTickDeltaTime: in deltaTime,
        decisions: ref decisions,
        intent: ref intent,
        position: in position,
        senses: in world,
        stamina: in stamina);

    foragerInstance.Tick(bb);

    // The caller owns the component, so the caller applies the decision.
    float before = position.X;
    if (intent.HasGoal)
    {
        float step01 = MathF.Sign(intent.GoalX - position.X);
        position = position with { X = position.X + step01 };
    }

    // Walking costs something, which eventually changes which branch the tree takes.
    stamina = stamina with { Value = MathF.Max(0f, stamina.Value - 0.2f) };

    Console.WriteLine(
        $"  step {step}: {intent.Kind,-6} goal x={intent.GoalX:0.0}"
        + $" -> moved {before:0.0} to {position.X:0.0}, stamina now {stamina.Value:0.0}");
}

// ---------------------------------------------------------------------------------------------
// 4. The layout IS the asset: a raw byte copy of the compiled blob, loaded back with no managed
//    tree in between. Node identity crosses the boundary as GUIDs; the process-local type ids
//    are re-resolved on load. Under PublishAot this is the whole ship-and-load path.
// ---------------------------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("Round-tripping the compiled layout through bytes:");

byte[] shipped = layout.SerializeToBytes();
using (BehaviorTreeLayout loaded = BehaviorTreeLayout.Deserialize(shipped))
{
    BehaviorTreeInstance loadedInstance = loaded.CreateInstance();

    var intent = default(Intent);
    var decisions = default(Decisions);
    var senses = new Senses { FoodVisible = true, FoodX = 4f };
    NodeState state = loadedInstance.Tick(ForagerTreeBlackboard.Bind(
        behaviorTreeTickDeltaTime: in deltaTime,
        decisions: ref decisions,
        intent: ref intent,
        position: in position,
        senses: in senses,
        stamina: in stamina));

    Console.WriteLine(
        $"  {shipped.Length} bytes -> {loaded.NodeCount} nodes -> {state}, intent {intent.Kind}");
}

layout.Dispose();
