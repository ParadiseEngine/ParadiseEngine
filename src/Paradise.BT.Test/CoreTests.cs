namespace Paradise.BT.Test;

public sealed class CoreTests
{
    // ============================
    // BehaviorTreeBuilder validation
    // ============================

    [Test]
    public async Task Builder_Rejects_Action_Node_With_Children()
    {
        InvalidOperationException? ex = null;
        try
        {
            BehaviorTreeBuilder.Build(
                BehaviorNodes.Node(new SuccessNode(), BehaviorNodeType.Action, BehaviorNodes.Success()));
        }
        catch (InvalidOperationException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message.Contains("Action", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Builder_Rejects_Decorator_With_Zero_Children()
    {
        InvalidOperationException? ex = null;
        try
        {
            BehaviorTreeBuilder.Build(
                BehaviorNodes.Node(new InverterNode(), BehaviorNodeType.Decorate));
        }
        catch (InvalidOperationException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message.Contains("Decorator", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Builder_Rejects_Decorator_With_Two_Children()
    {
        InvalidOperationException? ex = null;
        try
        {
            BehaviorTreeBuilder.Build(
                BehaviorNodes.Node(new InverterNode(), BehaviorNodeType.Decorate,
                    BehaviorNodes.Success(),
                    BehaviorNodes.Failure()));
        }
        catch (InvalidOperationException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message.Contains("Decorator", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Builder_Accepts_Composite_With_Any_Number_Of_Children()
    {
        // Composite with 0 children should also be valid (no constraint)
        BehaviorTree tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Node(new SequenceNode(), BehaviorNodeType.Composite));

        await Assert.That(tree.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Builder_Validates_Nested_Nodes()
    {
        // Invalid nested decorator (0 children) inside a valid sequence
        InvalidOperationException? ex = null;
        try
        {
            BehaviorTreeBuilder.Build(
                BehaviorNodes.Sequence(
                    BehaviorNodes.Node(new InverterNode(), BehaviorNodeType.Decorate)));
        }
        catch (InvalidOperationException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Builder_Instance_Method_Produces_Same_Result()
    {
        var definition = BehaviorNodes.Sequence(
            BehaviorNodes.Success(),
            BehaviorNodes.Failure());

        BehaviorTree tree = new BehaviorTreeBuilder(definition).Build();

        await Assert.That(tree.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Builder_Null_Root_Throws_ArgumentNullException()
    {
        ArgumentNullException? ex = null;
        try
        {
            _ = new BehaviorTreeBuilder(null!);
        }
        catch (ArgumentNullException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
    }

    // ============================
    // BehaviorTree public API
    // ============================

    [Test]
    public async Task BehaviorTree_Count_Matches_Total_Node_Count()
    {
        BehaviorTree tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Sequence(
                BehaviorNodes.Success(),
                BehaviorNodes.Inverter(BehaviorNodes.Failure())));

        // Sequence(1) + Success(1) + Inverter(1) + Failure(1) = 4
        await Assert.That(tree.Count).IsEqualTo(4);
    }

    [Test]
    public async Task BehaviorTree_GetNodeType_Returns_Correct_Types()
    {
        BehaviorTree tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Sequence(
                BehaviorNodes.Success()));

        await Assert.That(tree.GetNodeType(0)).IsEqualTo(typeof(SequenceNode));
        await Assert.That(tree.GetNodeType(1)).IsEqualTo(typeof(SuccessNode));
    }

    [Test]
    public async Task BehaviorTree_GetNodeBehaviorType_Returns_Correct_Kinds()
    {
        BehaviorTree tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Sequence(
                BehaviorNodes.Inverter(BehaviorNodes.Success())));

        await Assert.That(tree.GetNodeBehaviorType(0)).IsEqualTo(BehaviorNodeType.Composite);
        await Assert.That(tree.GetNodeBehaviorType(1)).IsEqualTo(BehaviorNodeType.Decorate);
        await Assert.That(tree.GetNodeBehaviorType(2)).IsEqualTo(BehaviorNodeType.Action);
    }

    [Test]
    public async Task BehaviorTree_GetEndIndex_Returns_Correct_Indices()
    {
        BehaviorTree tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Sequence(
                BehaviorNodes.Success(),
                BehaviorNodes.Failure()));

        // Sequence at 0 ends at 3 (total count)
        await Assert.That(tree.GetEndIndex(0)).IsEqualTo(3);
        // Success at 1 ends at 2
        await Assert.That(tree.GetEndIndex(1)).IsEqualTo(2);
        // Failure at 2 ends at 3
        await Assert.That(tree.GetEndIndex(2)).IsEqualTo(3);
    }

    [Test]
    public async Task BehaviorTree_GetNodeType_Out_Of_Range_Throws()
    {
        BehaviorTree tree = BehaviorTreeBuilder.Build(BehaviorNodes.Success());

        ArgumentOutOfRangeException? ex = null;
        try
        {
            tree.GetNodeType(5);
        }
        catch (ArgumentOutOfRangeException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task BehaviorTree_GetNodeType_Negative_Index_Throws()
    {
        BehaviorTree tree = BehaviorTreeBuilder.Build(BehaviorNodes.Success());

        ArgumentOutOfRangeException? ex = null;
        try
        {
            tree.GetNodeType(-1);
        }
        catch (ArgumentOutOfRangeException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
    }

    // ============================
    // BehaviorTreeInstance lifecycle
    // ============================

    [Test]
    public async Task Instance_Status_Reflects_Last_Tick_Result()
    {
        var tree = BehaviorTreeBuilder.Build(BehaviorNodes.Success());
        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());
        instance.AutoResetOnCompletion = false;

        instance.Tick();

        await Assert.That(instance.Status).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Instance_AutoReset_Disabled_Does_Not_Reset_On_Completion()
    {
        int executions = 0;
        var tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Action(_ =>
            {
                executions++;
                return NodeState.Success;
            }));

        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());
        instance.AutoResetOnCompletion = false;

        instance.Tick();
        instance.Tick();

        // Without auto-reset, the second tick sees the completed state and should
        // still return Success (the node is not re-executed)
        await Assert.That(instance.Status).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Instance_AutoReset_Enabled_Resets_And_Reticks()
    {
        int executions = 0;
        var tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Action(_ =>
            {
                executions++;
                return NodeState.Success;
            }));

        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());

        instance.Tick();
        instance.Tick();

        await Assert.That(executions).IsEqualTo(2);
    }

    [Test]
    public async Task Instance_Reset_Clears_State_To_Running()
    {
        var tree = BehaviorTreeBuilder.Build(BehaviorNodes.Running());
        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());
        instance.AutoResetOnCompletion = false;

        instance.Tick();
        await Assert.That(instance.Status).IsEqualTo(NodeState.Running);

        instance.Reset();
        // After reset, state is cleared to 0 (no flags set)
        await Assert.That(instance.Status).IsEqualTo((NodeState)0);
    }

    [Test]
    public async Task Instance_Tick_Sets_DeltaTime_On_Blackboard()
    {
        float capturedDeltaTime = 0f;
        var tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Action(bb =>
            {
                capturedDeltaTime = bb.GetData<BehaviorTreeTickDeltaTime>().Value;
                return NodeState.Success;
            }));

        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());
        instance.Tick(0.016f);

        await Assert.That(capturedDeltaTime).IsEqualTo(0.016f);
    }

    [Test]
    public async Task Instance_CreateInstance_Without_Blackboard_Works()
    {
        var tree = BehaviorTreeBuilder.Build(BehaviorNodes.Success());
        BehaviorTreeInstance instance = tree.CreateInstance();

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Instance_Blackboard_Ref_Is_Accessible()
    {
        var blackboard = new Blackboard();
        blackboard.SetData(42);

        var tree = BehaviorTreeBuilder.Build(BehaviorNodes.Success());
        BehaviorTreeInstance instance = tree.CreateInstance(blackboard);

        await Assert.That(instance.Blackboard.GetData<int>()).IsEqualTo(42);
    }

    // ============================
    // Complex tree scenarios
    // ============================

    [Test]
    public async Task Deep_Nested_Tree_Executes_Correctly()
    {
        // Sequence -> Inverter -> Failure = Sequence(Inverter(Failure))
        // Inverter turns Failure into Success, Sequence with one child returns that
        var tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Sequence(
                BehaviorNodes.Inverter(BehaviorNodes.Failure()),
                BehaviorNodes.Succeeder(BehaviorNodes.Failure())));

        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());

        // Both children return Success -> Sequence returns Success
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Selector_With_Running_Then_Success_Returns_Running_First()
    {
        int tickCount = 0;
        var tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Selector(
                BehaviorNodes.Action(_ =>
                {
                    tickCount++;
                    return tickCount == 1 ? NodeState.Running : NodeState.Failure;
                }),
                BehaviorNodes.Success()));

        BehaviorTreeInstance instance = tree.CreateInstance(new Blackboard());

        // First tick: first child is Running -> Selector returns Running
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        // Second tick: first child fails, second child succeeds -> Success
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    // ============================
    // BehaviorNodes factory inference
    // ============================

    [Test]
    public async Task BehaviorNodes_Node_Infers_Action_Type_With_No_Children()
    {
        BehaviorTree tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Node(new SuccessNode()));

        await Assert.That(tree.GetNodeBehaviorType(0)).IsEqualTo(BehaviorNodeType.Action);
    }

    [Test]
    public async Task BehaviorNodes_Node_Infers_Decorate_Type_With_One_Child()
    {
        BehaviorTree tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Node(new InverterNode(), BehaviorNodes.Success()));

        await Assert.That(tree.GetNodeBehaviorType(0)).IsEqualTo(BehaviorNodeType.Decorate);
    }

    [Test]
    public async Task BehaviorNodes_Node_Infers_Composite_Type_With_Multiple_Children()
    {
        BehaviorTree tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Node(new SequenceNode(), BehaviorNodes.Success(), BehaviorNodes.Success()));

        await Assert.That(tree.GetNodeBehaviorType(0)).IsEqualTo(BehaviorNodeType.Composite);
    }
}
