namespace Paradise.BT.Test;

public sealed class BehaviorTreeTests
{
    private struct ResetCallData
    {
        public int Value;
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
                BehaviorNodes.Delay(0.5f),
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
            BehaviorNodes.Action(_ =>
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
                BehaviorNodes.Action(_ =>
                {
                    leftRuns++;
                    return NodeState.Success;
                }),
                BehaviorNodes.Action(_ =>
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
                BehaviorNodes.Action(_ =>
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
                BehaviorNodes.Action(_ =>
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
    public async Task Custom_Struct_Node_Can_Be_Authored_Through_Interface_Constraints()
    {
        var tree = BehaviorTreeBuilder.Build(BehaviorNodes.Node(new CountingNode()));
        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
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
}
