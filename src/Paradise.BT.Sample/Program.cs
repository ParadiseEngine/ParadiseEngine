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
// Nothing below names a blackboard type that anyone wrote. ForagerTreeBlackboard and
// ForagerTreeExtras were emitted from what the tree's eleven node types declare with
// [Reads<T>] / [Writes<T>], checked against ForagerRow's claims.
//
// Running it under PublishAot is the part worth having: generated code plus trimming plus native
// compilation is where this would break first if it were going to.
// ---------------------------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("Forager — the generated blackboard, over a row:");

BehaviorTree forager = ForagerTree.Build();
BehaviorTreeLayout layout = BehaviorTreeLayout.Build(forager);
var states = new NodeState[layout.NodeCount];
var data = new byte[layout.RuntimeDataSize];
UnmanagedNodeBlob.Initialize(layout.Handle, states, data);

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

    // Extras carry what the tree WRITES; everything it reads is a parameter, delta time included.
    var extras = default(ForagerTreeExtras);
    var bb = ForagerTreeBlackboard.Bind(
        behaviorTreeTickDeltaTime: deltaTime,
        position: position,
        senses: senses,
        stamina: stamina,
        extras: extras);
    var blob = new UnmanagedNodeBlob(layout.Handle, states, data);

    if (blob.GetState(0).IsCompleted())
    {
        VirtualMachine.Reset(ref blob, ref bb);
    }

    NodeState state = VirtualMachine.Tick(ref blob, ref bb);
    Intent intent = bb.Extras.Intent;

    Console.WriteLine(
        $"  {label,-14} stamina {energy:0.0} -> {state,-7} {intent.Kind} "
        + (intent.HasGoal ? $"goal x={intent.GoalX:0.0}" : "no goal")
        + $"  (decisions so far: {bb.Extras.Decisions.Count})");
}

// ---------------------------------------------------------------------------------------------
// 3. How a tree CHANGES anything, given that components are read-only to it.
//
// A node cannot write Position: components bind by value, so the write would not reach the chunk,
// and PBT0008 refuses a node that tries. What a node writes is a CONCLUSION — here Intent — which
// the caller applies. In the game that caller is EnemySystem, turning the goal into a steering
// intent; here it is this loop, walking the forager toward whatever the tree decided.
//
// That round trip is the point: read components, write extras, apply, read again. It is also why
// the same tree can drive a body steered any way you like.
// ---------------------------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("Walking toward what the tree decides:");

position = new Position { X = 0f };
stamina = new Stamina { Value = 0.9f };
var world = new Senses { FoodVisible = true, FoodX = 4f };

for (int step = 1; step <= 5; step++)
{
    var extras = default(ForagerTreeExtras);
    var bb = ForagerTreeBlackboard.Bind(
        behaviorTreeTickDeltaTime: deltaTime,
        position: position,
        senses: world,
        stamina: stamina,
        extras: extras);
    var blob = new UnmanagedNodeBlob(layout.Handle, states, data);

    if (blob.GetState(0).IsCompleted())
    {
        VirtualMachine.Reset(ref blob, ref bb);
    }

    VirtualMachine.Tick(ref blob, ref bb);
    Intent intent = bb.Extras.Intent;

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
