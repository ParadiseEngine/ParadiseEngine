namespace Paradise.BT.Test;

public sealed class BehaviorTreeTests
{
    private struct ResetCallData
    {
        public int Value;
    }

    private struct PreTickData
    {
        public int Value;
    }

    [System.Runtime.InteropServices.Guid("F4E3D2C1-B0A9-4867-8765-432109FEDCBA")]
    private struct ReadBlackboardNode : INodeData
    {
        public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
            where TNodeBlob : struct, INodeBlob
            where TBlackboard : struct, IBlackboard
        {
            var data = bb.GetData<PreTickData>();
            return data.Value == 42 ? NodeState.Success : NodeState.Failure;
        }
    }

    [System.Runtime.InteropServices.Guid("A1523157-2737-48A0-8F1D-14D07B5F4D77")]
    private struct CountingNode : INodeData
    {
        public int Count;

        public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
            where TNodeBlob : struct, INodeBlob
            where TBlackboard : struct, IBlackboard
        {
            Count++;
            return Count >= 2 ? NodeState.Success : NodeState.Running;
        }
    }

    [System.Runtime.InteropServices.Guid("324C79B0-5CAB-4953-9A3F-9490C6361AE5")]
    private struct ResetAwareNode : INodeData, ICustomResetAction
    {
        public int Count;

        public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
            where TNodeBlob : struct, INodeBlob
            where TBlackboard : struct, IBlackboard
        {
            Count++;
            return Count >= 2 ? NodeState.Success : NodeState.Running;
        }

        public void Reset<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
            where TNodeBlob : struct, INodeBlob
            where TBlackboard : struct, IBlackboard
        {
            ref ResetCallData resetCall = ref bb.GetDataRef<ResetCallData>();
            resetCall.Value++;
        }
    }

    [Test]
    public async Task Sequence_With_Delay_Completes_After_Enough_Time()
    {
        var tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Sequence(
                TestBehaviorNodes.Delay(0.5f),
                BehaviorNodes.Success()));

        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick(0.2f)).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick(0.2f)).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick(0.2f)).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Completed_Root_Auto_Resets_On_Next_Tick()
    {
        int runs = 0;
        var tree = BehaviorTreeBuilder.Build(
            TestBehaviorNodes.Action(_ =>
            {
                runs++;
                return NodeState.Success;
            }));

        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(runs).IsEqualTo(2);
    }

    [Test]
    public async Task Selector_Stops_After_First_Success()
    {
        int leftRuns = 0;
        int rightRuns = 0;
        var tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Selector(
                TestBehaviorNodes.Action(_ =>
                {
                    leftRuns++;
                    return NodeState.Success;
                }),
                TestBehaviorNodes.Action(_ =>
                {
                    rightRuns++;
                    return NodeState.Success;
                })));

        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());
        NodeState status = instance.Tick();

        await Assert.That(status).IsEqualTo(NodeState.Success);
        await Assert.That(leftRuns).IsEqualTo(1);
        await Assert.That(rightRuns).IsEqualTo(0);
    }

    [Test]
    public async Task Repeat_Completes_After_Configured_Number_Of_Successes()
    {
        int executions = 0;
        var tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Repeat(
                3,
                TestBehaviorNodes.Action(_ =>
                {
                    executions++;
                    return NodeState.Success;
                })));

        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(executions).IsEqualTo(3);
    }

    [Test]
    public async Task Repeat_With_MultiTick_Child_Completes_Correct_Number_Of_Times()
    {
        int completions = 0;
        int tickCount = 0;
        var tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Repeat(
                3,
                TestBehaviorNodes.Action(_ =>
                {
                    tickCount++;
                    if (tickCount % 2 == 0)
                    {
                        completions++;
                        return NodeState.Success;
                    }

                    return NodeState.Running;
                })));

        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());

        // Each child completion takes 2 ticks, 3 completions = 6 ticks minimum
        // Tick 1: child Running (tick 1 of completion 1)
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        // Tick 2: child Success (completion 1), TickTimes 3->2
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        // Tick 3: child reset + re-tick -> Running (tick 1 of completion 2)
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        // Tick 4: child Success (completion 2), TickTimes 2->1
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        // Tick 5: child reset + re-tick -> Running (tick 1 of completion 3)
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        // Tick 6: child Success (completion 3), TickTimes 1->0 -> Success
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);

        await Assert.That(completions).IsEqualTo(3);
    }

    [Test]
    public async Task Parallel_Returns_Failure_When_Any_Child_Fails_And_None_Are_Running()
    {
        var tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Parallel(
                BehaviorNodes.Success(),
                BehaviorNodes.Failure()));

        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Parallel_Preserves_Completed_Children_State()
    {
        // Child 1: instant Success. Child 2: Running on tick 1, Success on tick 2.
        var tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Parallel(
                BehaviorNodes.Success(),
                BehaviorNodes.Node(new CountingNode())));

        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());

        // Tick 1: child2 returns Running → Parallel returns Running
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);

        // Tick 2: child1 already completed (Success preserved), child2 now completes → Success
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Parallel_All_Children_Already_Completed_Returns_Valid_State()
    {
        var tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Parallel(
                BehaviorNodes.Success(),
                BehaviorNodes.Failure()));

        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());
        instance.AutoResetOnCompletion = false;

        // Tick 1: both children complete → Failure (because one child failed)
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);

        // Tick 2: all children already completed, no reset → should still return valid state, not 0
        NodeState secondTick = instance.Tick();
        await Assert.That(secondTick).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Parallel_Preserves_Failed_Child_State_Alongside_Running_Child()
    {
        // Child 1: instant Failure. Child 2: Running on tick 1, Success on tick 2.
        var tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Parallel(
                BehaviorNodes.Failure(),
                BehaviorNodes.Node(new CountingNode())));

        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());

        // Tick 1: child1 Failure + child2 Running → Running (Running takes priority)
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);

        // Tick 2: child1 already completed (Failure preserved), child2 completes (Success) → Failure
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Custom_Struct_Node_Can_Be_Authored_Through_Interface_Constraints()
    {
        var tree = BehaviorTreeBuilder.Build(BehaviorNodes.Node(new CountingNode()));
        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Sequence_Returns_Failure_Not_Zero_When_Child_Already_Failed()
    {
        var tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Sequence(
                BehaviorNodes.Failure(),
                BehaviorNodes.Success()));

        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());
        instance.AutoResetOnCompletion = false;

        // First tick: Failure child breaks the sequence -> returns Failure
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);

        // Second tick: child is already completed (Failure), should still return Failure not 0
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Selector_Returns_Success_Not_Zero_When_Child_Already_Succeeded()
    {
        var tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Selector(
                BehaviorNodes.Success(),
                BehaviorNodes.Failure()));

        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());
        instance.AutoResetOnCompletion = false;

        // First tick: Success child breaks the selector -> returns Success
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);

        // Second tick: child is already completed (Success), should still return Success not 0
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Sequence_Returns_Success_On_Retick_When_All_Children_Already_Succeeded()
    {
        var tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Sequence(
                BehaviorNodes.Success(),
                BehaviorNodes.Success()));

        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());
        instance.AutoResetOnCompletion = false;

        // First tick: both children succeed, sequence returns Success
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);

        // Second tick: all children already completed (no break triggered), should return Success not 0
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Custom_Reset_Action_Runs_When_Tree_Resets()
    {
        var blackboard = new Blackboard();
        blackboard.SetData(new ResetCallData());

        var tree = BehaviorTreeBuilder.Build(BehaviorNodes.Node(new ResetAwareNode()));
        BehaviorTreeInstance instance = tree.CreateInstance(blackboard);

        await Assert.That(instance.Blackboard.GetData<ResetCallData>().Value).IsEqualTo(1);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(instance.Blackboard.GetData<ResetCallData>().Value).IsEqualTo(1);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Blackboard.GetData<ResetCallData>().Value).IsEqualTo(2);
    }

    [Test]
    public async Task Blackboard_Mutations_Before_First_Tick_Are_Preserved()
    {
        var tree = BehaviorTreeBuilder.Build(BehaviorNodes.Node(new ReadBlackboardNode()));
        BehaviorTreeInstance instance = tree.CreateInstance();

        // Set data BEFORE first tick — this is the bug scenario
        instance.Blackboard.SetData(new PreTickData { Value = 42 });

        // The node returns Success only if it reads Value == 42 from the blackboard
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }
}
