namespace Paradise.BT.Test;

public sealed class NodeTests
{
    // ============================
    // SequenceNode
    // ============================

    [Test]
    public async Task Sequence_All_Children_Succeed_Returns_Success()
    {
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Sequence(
                BuiltInBehaviorNodes.Success(),
                BuiltInBehaviorNodes.Success(),
                BuiltInBehaviorNodes.Success()));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Sequence_First_Child_Fails_Returns_Failure_Without_Ticking_Rest()
    {
        int secondChildTicked = 0;
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Sequence(
                BuiltInBehaviorNodes.Failure(),
                TestBehaviorNodes.Action(_ =>
                {
                    secondChildTicked++;
                    return NodeState.Success;
                })));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
        await Assert.That(secondChildTicked).IsEqualTo(0);
    }

    [Test]
    public async Task Sequence_Running_Child_Resumes_On_Next_Tick()
    {
        int tickCount = 0;
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Sequence(
                TestBehaviorNodes.Action(_ =>
                {
                    tickCount++;
                    return tickCount >= 2 ? NodeState.Success : NodeState.Running;
                }),
                BuiltInBehaviorNodes.Success()));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Sequence_Resumes_From_Running_Child_Skipping_Completed_Siblings()
    {
        int firstChildTicks = 0;
        int secondChildTicks = 0;
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Sequence(
                TestBehaviorNodes.Action(_ =>
                {
                    firstChildTicks++;
                    return NodeState.Success;
                }),
                TestBehaviorNodes.Action(_ =>
                {
                    secondChildTicks++;
                    return secondChildTicks >= 2 ? NodeState.Success : NodeState.Running;
                })));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        // Tick 1: first child succeeds, second child returns running
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(firstChildTicks).IsEqualTo(1);
        await Assert.That(secondChildTicks).IsEqualTo(1);

        // Tick 2: first child already completed (not re-ticked), second child now succeeds
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(firstChildTicks).IsEqualTo(1);
        await Assert.That(secondChildTicks).IsEqualTo(2);
    }

    // ============================
    // SelectorNode
    // ============================

    [Test]
    public async Task Selector_All_Children_Fail_Returns_Failure()
    {
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Selector(
                BuiltInBehaviorNodes.Failure(),
                BuiltInBehaviorNodes.Failure(),
                BuiltInBehaviorNodes.Failure()));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Selector_First_Child_Succeeds_Stops_Immediately()
    {
        int secondChildTicked = 0;
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Selector(
                BuiltInBehaviorNodes.Success(),
                TestBehaviorNodes.Action(_ =>
                {
                    secondChildTicked++;
                    return NodeState.Failure;
                })));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(secondChildTicked).IsEqualTo(0);
    }

    [Test]
    public async Task Selector_Running_Child_Resumes_On_Next_Tick()
    {
        int tickCount = 0;
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Selector(
                TestBehaviorNodes.Action(_ =>
                {
                    tickCount++;
                    return tickCount >= 2 ? NodeState.Success : NodeState.Running;
                }),
                BuiltInBehaviorNodes.Failure()));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Selector_Skips_Failed_Children_And_Tries_Next()
    {
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Selector(
                BuiltInBehaviorNodes.Failure(),
                BuiltInBehaviorNodes.Failure(),
                BuiltInBehaviorNodes.Success()));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    // ============================
    // ParallelNode
    // ============================

    [Test]
    public async Task Parallel_All_Children_Succeed_Returns_Success()
    {
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Parallel(
                BuiltInBehaviorNodes.Success(),
                BuiltInBehaviorNodes.Success(),
                BuiltInBehaviorNodes.Success()));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Parallel_All_Children_Fail_Returns_Failure()
    {
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Parallel(
                BuiltInBehaviorNodes.Failure(),
                BuiltInBehaviorNodes.Failure()));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Parallel_Running_Takes_Priority_Over_Success_And_Failure()
    {
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Parallel(
                BuiltInBehaviorNodes.Success(),
                BuiltInBehaviorNodes.Running(),
                BuiltInBehaviorNodes.Failure()));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
    }

    // ============================
    // RepeatTimesNode
    // ============================

    [Test]
    public async Task RepeatTimes_Zero_Repeats_Returns_Success_Immediately()
    {
        int executions = 0;
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Repeat(
                0,
                TestBehaviorNodes.Action(_ =>
                {
                    executions++;
                    return NodeState.Success;
                })));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        // With 0 repeats, child is ticked once and TickTimes goes from 0 to -1, returning Success
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task RepeatTimes_BreakStates_Stops_On_Failure()
    {
        int executions = 0;
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Repeat(
                5,
                TestBehaviorNodes.Action(_ =>
                {
                    executions++;
                    return executions == 2 ? NodeState.Failure : NodeState.Success;
                }),
                breakStates: NodeState.Failure));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        // Tick 1: child returns Success, TickTimes 5->4 -> Running
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        // Tick 2: child returns Failure, break -> Failure
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
        await Assert.That(executions).IsEqualTo(2);
    }

    [Test]
    public async Task RepeatTimes_One_Repeat_Succeeds_On_First_Completion()
    {
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Repeat(
                1,
                BuiltInBehaviorNodes.Success()));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    // ============================
    // RepeatForeverNode
    // ============================

    [Test]
    public async Task RepeatForever_Keeps_Running_On_Child_Success()
    {
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.RepeatForever(
                BuiltInBehaviorNodes.Success()));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());
        instance.AutoResetOnCompletion = false;

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
    }

    [Test]
    public async Task RepeatForever_Keeps_Running_On_Child_Failure()
    {
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.RepeatForever(
                BuiltInBehaviorNodes.Failure()));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());
        instance.AutoResetOnCompletion = false;

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
    }

    [Test]
    public async Task RepeatForever_BreakStates_Stops_On_Failure()
    {
        int executions = 0;
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.RepeatForever(
                TestBehaviorNodes.Action(_ =>
                {
                    executions++;
                    return executions == 3 ? NodeState.Failure : NodeState.Success;
                }),
                breakStates: NodeState.Failure));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task RepeatForever_BreakStates_Stops_On_Success()
    {
        int executions = 0;
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.RepeatForever(
                TestBehaviorNodes.Action(_ =>
                {
                    executions++;
                    return executions == 2 ? NodeState.Success : NodeState.Failure;
                }),
                breakStates: NodeState.Success));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    // ============================
    // InverterNode
    // ============================

    [Test]
    public async Task Inverter_Inverts_Success_To_Failure()
    {
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Inverter(BuiltInBehaviorNodes.Success()));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Inverter_Inverts_Failure_To_Success()
    {
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Inverter(BuiltInBehaviorNodes.Failure()));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Inverter_Passes_Through_Running()
    {
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Inverter(BuiltInBehaviorNodes.Running()));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
    }

    // ============================
    // SucceederNode
    // ============================

    [Test]
    public async Task Succeeder_Converts_Failure_To_Success()
    {
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Succeeder(BuiltInBehaviorNodes.Failure()));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Succeeder_Passes_Through_Success()
    {
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Succeeder(BuiltInBehaviorNodes.Success()));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Succeeder_Passes_Through_Running()
    {
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Succeeder(BuiltInBehaviorNodes.Running()));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
    }

    // ============================
    // DelayTimerNode
    // ============================

    [Test]
    public async Task Delay_Zero_Seconds_Succeeds_Immediately()
    {
        var tree = BehaviorTreeBuilder.Build(BuiltInBehaviorNodes.Delay(0f));
        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Delay_Exact_Boundary_Succeeds()
    {
        var tree = BehaviorTreeBuilder.Build(BuiltInBehaviorNodes.Delay(1.0f));
        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick(0.5f)).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick(0.5f)).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Delay_Overshoot_Still_Succeeds()
    {
        var tree = BehaviorTreeBuilder.Build(BuiltInBehaviorNodes.Delay(0.3f));
        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick(1.0f)).IsEqualTo(NodeState.Success);
    }

    // ============================
    // DelegateActionNode
    // ============================

    [Test]
    public async Task DelegateAction_Returns_Success()
    {
        var tree = BehaviorTreeBuilder.Build(
            TestBehaviorNodes.Action(_ => NodeState.Success));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task DelegateAction_Returns_Failure()
    {
        var tree = BehaviorTreeBuilder.Build(
            TestBehaviorNodes.Action(_ => NodeState.Failure));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task DelegateAction_Returns_Running()
    {
        var tree = BehaviorTreeBuilder.Build(
            TestBehaviorNodes.Action(_ => NodeState.Running));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
    }

    [Test]
    public async Task DelegateAction_With_Index_Receives_Correct_Index()
    {
        int receivedIndex = -1;
        var tree = BehaviorTreeBuilder.Build(
            TestBehaviorNodes.Action((_, index) =>
            {
                receivedIndex = index;
                return NodeState.Success;
            }));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());
        instance.Tick();

        // Root node is at index 0
        await Assert.That(receivedIndex).IsEqualTo(0);
    }

    [Test]
    public async Task DelegateAction_Receives_Blackboard()
    {
        var tree = BehaviorTreeBuilder.Build(
            TestBehaviorNodes.Action(bb =>
            {
                int value = bb.GetData<int>();
                return value == 42 ? NodeState.Success : NodeState.Failure;
            }));

        var blackboard = new Blackboard();
        blackboard.SetData(42);
        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(blackboard);

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    // ============================
    // DelegateConditionNode
    // ============================

    [Test]
    public async Task DelegateCondition_True_Returns_Success()
    {
        var tree = BehaviorTreeBuilder.Build(
            TestBehaviorNodes.Condition(_ => true));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task DelegateCondition_False_Returns_Failure()
    {
        var tree = BehaviorTreeBuilder.Build(
            TestBehaviorNodes.Condition(_ => false));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task DelegateCondition_With_Index_Receives_Correct_Index()
    {
        int receivedIndex = -1;
        var tree = BehaviorTreeBuilder.Build(
            TestBehaviorNodes.Condition((_, index) =>
            {
                receivedIndex = index;
                return true;
            }));

        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());
        instance.Tick();

        await Assert.That(receivedIndex).IsEqualTo(0);
    }

    // ============================
    // SuccessNode, FailedNode, RunningNode
    // ============================

    [Test]
    public async Task SuccessNode_Always_Returns_Success()
    {
        var tree = BehaviorTreeBuilder.Build(BuiltInBehaviorNodes.Success());
        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task FailedNode_Always_Returns_Failure()
    {
        var tree = BehaviorTreeBuilder.Build(BuiltInBehaviorNodes.Failure());
        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task RunningNode_Always_Returns_Running()
    {
        var tree = BehaviorTreeBuilder.Build(BuiltInBehaviorNodes.Running());
        BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
    }

    // ============================
    // NodeState Extensions
    // ============================

    [Test]
    public async Task NodeState_IsCompleted_True_For_Success_And_Failure()
    {
        await Assert.That(NodeState.Success.IsCompleted()).IsTrue();
        await Assert.That(NodeState.Failure.IsCompleted()).IsTrue();
        await Assert.That(NodeState.Running.IsCompleted()).IsFalse();
    }

    [Test]
    public async Task NodeState_HasFlagFast_Works_With_Combined_Flags()
    {
        NodeState combined = NodeState.Success | NodeState.Failure;
        await Assert.That(combined.HasFlagFast(NodeState.Success)).IsTrue();
        await Assert.That(combined.HasFlagFast(NodeState.Failure)).IsTrue();
        await Assert.That(combined.HasFlagFast(NodeState.Running)).IsFalse();
    }

    [Test]
    public async Task NodeState_ToNodeState_Converts_Bool()
    {
        await Assert.That(true.ToNodeState()).IsEqualTo(NodeState.Success);
        await Assert.That(false.ToNodeState()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task NodeState_IsRunningOrFailure_Returns_Correct_Values()
    {
        await Assert.That(NodeState.Running.IsRunningOrFailure()).IsTrue();
        await Assert.That(NodeState.Failure.IsRunningOrFailure()).IsTrue();
        await Assert.That(NodeState.Success.IsRunningOrFailure()).IsFalse();
    }

    [Test]
    public async Task NodeState_IsRunningOrSuccess_Returns_Correct_Values()
    {
        await Assert.That(NodeState.Running.IsRunningOrSuccess()).IsTrue();
        await Assert.That(NodeState.Success.IsRunningOrSuccess()).IsTrue();
        await Assert.That(NodeState.Failure.IsRunningOrSuccess()).IsFalse();
    }
}
