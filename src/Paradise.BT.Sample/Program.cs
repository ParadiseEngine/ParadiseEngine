using Paradise.BT;
using Paradise.BT.Nodes;
using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;
using Paradise.BT.Sample;
using Paradise.BT.Sample.Builder;

// ---------------------------------------------------------------------------------------------
// 1. The builder DSL, over a hand-written blackboard.
// ---------------------------------------------------------------------------------------------

var blackboard = new Blackboard();
blackboard.SetData(new HasTargetData { Value = true });
blackboard.SetData(new ShotsFiredData());

// Every leaf is an unmanaged struct declared in SampleNodes.cs; the builder classes around them
// are generated from [Builder]. Build() compiles straight to the shared layout.
using var tree = new Selector(
    new Sequence(
        new HasTarget(),
        new Repeat(
            3,
            new Sequence(
                new Delay(0.5f),
                new FireShot()))),
    new Idle()
).Build();

// An instance is just two caller-owned buffers over the shared layout; a BehaviorTreeRef is
// built where it is used and the blackboard is passed per tick — the shape that also fits the
// generated ref-struct blackboards below.
var states = new NodeState[tree.Blob.Count];
var data = new byte[tree.Blob.DataSize];
BehaviorTreeRef Tree() => new(ref tree.Blob, states, data);

VirtualMachine.Reset(Tree(), blackboard);

for (int i = 0; i < 10; i++)
{
    blackboard.SetData(new TickDeltaTime(0.25f));
    if (Tree().GetState(0).IsCompleted())
    {
        VirtualMachine.Reset(Tree(), blackboard);
    }

    NodeState status = VirtualMachine.Tick(Tree(), blackboard);
    Console.WriteLine($"Tick {i + 1}: {status}");
}

// ---------------------------------------------------------------------------------------------
// 2. The GENERATED blackboard, over an ECS row.
//
// Nothing below names a blackboard type that anyone wrote. ForagerTreeBlackboard was emitted from
// what the tree's node types touch — the union of their access is the whole contract. It holds a
// ref to each value, so a write lands in the local passed to Bind.
//
// Running it under PublishAot is the part worth having: generated code plus trimming plus native
// compilation is where this would break first if it were going to.
// ---------------------------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("Forager — the generated blackboard, over a row:");

BehaviorTreeLayout layout = BehaviorTrees.Compile<ForagerTree>();

// The two per-agent buffers; the blackboard is bound per tick, because a generated blackboard
// is a ref struct no field can hold.
var foragerStates = new NodeState[layout.Blob.Count];
var foragerData = new byte[layout.Blob.DataSize];
BehaviorTreeRef Forager() => new(ref layout.Blob, foragerStates, foragerData);
Forager().ResetRuntimeData(0, layout.Blob.Count);

Console.WriteLine($"  {layout.Blob.Count} nodes, {layout.Blob.DataSize} bytes of node data.");

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

var deltaTime = new TickDeltaTime(0.5f);

foreach ((string label, Senses senses, float energy) in situations)
{
    stamina = stamina with { Value = energy };

    // The tree writes straight into these: the blackboard holds a ref to each.
    var intent = default(Intent);
    var decisions = default(Decisions);
    var bb = ForagerTreeBlackboard.Bind(
        tickDeltaTime: in deltaTime,
        decisions: ref decisions,
        intent: ref intent,
        position: in position,
        senses: in senses,
        stamina: in stamina);

    if (Forager().GetState(0).IsCompleted())
    {
        VirtualMachine.Reset(Forager(), bb);
    }

    NodeState state = VirtualMachine.Tick(Forager(), bb);

    Console.WriteLine(
        $"  {label,-14} stamina {energy:0.0} -> {state,-7} {intent.Kind} "
        + (intent.HasGoal ? $"goal x={intent.GoalX:0.0}" : "no goal")
        + $"  (decisions so far: {decisions.Count})");
}

// ---------------------------------------------------------------------------------------------
// 3. How a tree CHANGES anything, given that components are read-only to it.
//
// A node cannot write Position: it is a component, and components bind read-only by value
// (PBT0008). What these nodes write is a CONCLUSION, Intent, which the caller applies. In the
// game that caller is EnemySystem, turning the goal into a steering intent; here it is this
// loop, walking the forager toward whatever the tree decided.
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
        tickDeltaTime: in deltaTime,
        decisions: ref decisions,
        intent: ref intent,
        position: in position,
        senses: in world,
        stamina: in stamina);

    if (Forager().GetState(0).IsCompleted())
    {
        VirtualMachine.Reset(Forager(), bb);
    }

    VirtualMachine.Tick(Forager(), bb);

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

layout.Dispose();
