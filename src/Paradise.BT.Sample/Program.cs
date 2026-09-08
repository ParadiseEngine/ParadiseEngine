using Paradise.BT;
using Paradise.BT.Nodes;
using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;
using Paradise.BT.Sample;
using Paradise.BT.Sample.Builder;

// 1. Builder DSL with a manual blackboard.

var blackboard = new Blackboard();
blackboard.SetData(new HasTargetData { Value = true });
blackboard.SetData(new ShotsFiredData());

// Builder attributes generate wrappers; Build compiles them into a shared layout.
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

// Each instance owns two buffers; the borrowed view and blackboard are passed per tick.
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

// 2. Generated blackboard over ECS data. Ref fields write directly into Bind arguments.
// PublishAot validates generation, trimming and native compilation together.

Console.WriteLine();
Console.WriteLine("Forager — the generated blackboard, over a row:");

BehaviorTreeLayout<ForagerTree> layout = BehaviorTrees.Compile<ForagerTree>();

// Inline buffers hold instance state; the generated blackboard binds per tick.
// The typed layout accepts only ForagerTreeBlackboard.
var foragerStates = new NodeState[layout.Untyped.Blob.Count];
var foragerData = new byte[layout.Untyped.Blob.DataSize];
BehaviorTreeRef<ForagerTree> Forager() => layout.Ref(foragerStates, foragerData);
Forager().Untyped.ResetRuntimeData(0, layout.Untyped.Blob.Count);

Console.WriteLine($"  {layout.Untyped.Blob.Count} nodes, {layout.Untyped.Blob.DataSize} bytes of node data.");

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

    if (Forager().Status.IsCompleted())
    {
        Forager().Reset(bb);
    }

    NodeState state = Forager().Tick(bb);

    Console.WriteLine(
        $"  {label,-14} stamina {energy:0.0} -> {state,-7} {intent.Kind} "
        + (intent.HasGoal ? $"goal x={intent.GoalX:0.0}" : "no goal")
        + $"  (decisions so far: {decisions.Count})");
}

// 3. Nodes read components and write Intent; this loop applies the chosen movement.

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

    if (Forager().Status.IsCompleted())
    {
        Forager().Reset(bb);
    }

    Forager().Tick(bb);

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
