using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Paradise.BT.Generators;

namespace Paradise.BT.Generators.Test;

/// <summary>
/// The guard that makes <c>[Reads&lt;T&gt;]</c> / <c>[Writes&lt;T&gt;]</c> checked rather than
/// merely conventional. Each test here was confirmed to FAIL with the analyzer's report suppressed
/// — a diagnostic test that has never failed is a guard nobody has checked.
/// </summary>
public class BlackboardAccessAnalyzerTests
{
    /// <summary>
    /// Enough of Paradise.BT to declare a node. <c>SetData</c> rather than a ref-returning
    /// accessor is what makes the read/write split decidable at all: taking a ref to avoid a copy
    /// and taking one to mutate look identical.
    /// </summary>
    private const string Prelude = """
        using System;

        namespace Paradise.BT
        {
            public enum NodeState { Success, Failure, Running }

            public interface IBehaviorTree { }

            public interface IBlackboard
            {
                bool HasData<T>() where T : struct;
                T GetData<T>() where T : struct;
                void SetData<T>(T value) where T : struct;
            }

            public interface INode
            {
                NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
                    where TBehaviorTree : struct, IBehaviorTree
                    where TBlackboard : struct, IBlackboard;
            }

            [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
            public sealed class ReadsAttribute<T> : Attribute where T : struct { }

            [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
            public sealed class WritesAttribute<T> : Attribute where T : struct { }
        }

        namespace Game
        {
            public struct Pose { public float X; }
            public struct Decision { public bool Strike; }
        }
        """;

    private static CSharpAnalyzerTest<BlackboardAccessAnalyzer, DefaultVerifier> Test(string code)
        => new() { TestCode = Prelude + code };

    [Test]
    public async Task Reading_Something_Undeclared_Is_Reported()
    {
        var test = Test("""
            namespace Game
            {
                // Declares SOMETHING, which is what opts a node into the strict reading. A node
                // declaring nothing at all is read off its body by the generator instead.
                [Paradise.BT.Reads<Decision>]
                public struct PeekNode : Paradise.BT.INode
                {
                    public Paradise.BT.NodeState Tick<TBehaviorTree, TBlackboard>(
                        int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, Paradise.BT.IBehaviorTree
                        where TBlackboard : struct, Paradise.BT.IBlackboard
                    {
                        _ = bb.GetData<Decision>().Strike;
                        _ = bb.GetData<Pose>().X;
                        return Paradise.BT.NodeState.Success;
                    }
                }
            }
            """);

        test.ExpectedDiagnostics.Add(
            CSharpAnalyzerVerifier<BlackboardAccessAnalyzer, DefaultVerifier>
                .Diagnostic(BlackboardAccessAnalyzer.s_undeclaredAccess)
                .WithArguments("PeekNode", "GetData", "Pose", "Reads")
                .WithSpan(47, 17, 47, 35));

        await test.RunAsync().ConfigureAwait(false);
    }

    [Test]
    public async Task Writing_Something_Declared_Only_As_Read_Is_Reported()
    {
        // The asymmetry that matters: reading is not permission to write.
        var test = Test("""
            namespace Game
            {
                [Paradise.BT.Reads<Decision>]
                public struct StrikeNode : Paradise.BT.INode
                {
                    public Paradise.BT.NodeState Tick<TBehaviorTree, TBlackboard>(
                        int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, Paradise.BT.IBehaviorTree
                        where TBlackboard : struct, Paradise.BT.IBlackboard
                    {
                        bb.SetData(bb.GetData<Decision>() with { Strike = true });
                        return Paradise.BT.NodeState.Success;
                    }
                }
            }
            """);

        test.ExpectedDiagnostics.Add(
            CSharpAnalyzerVerifier<BlackboardAccessAnalyzer, DefaultVerifier>
                .Diagnostic(BlackboardAccessAnalyzer.s_undeclaredAccess)
                .WithArguments("StrikeNode", "SetData", "Decision", "Writes")
                .WithSpan(44, 13, 44, 70));

        await test.RunAsync().ConfigureAwait(false);
    }

    [Test]
    public async Task Declared_Access_Is_Accepted_And_Writes_Also_Permit_Reading()
    {
        var test = Test("""
            namespace Game
            {
                [Paradise.BT.Reads<Pose>]
                [Paradise.BT.Writes<Decision>]
                public struct SeekNode : Paradise.BT.INode
                {
                    public Paradise.BT.NodeState Tick<TBehaviorTree, TBlackboard>(
                        int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, Paradise.BT.IBehaviorTree
                        where TBlackboard : struct, Paradise.BT.IBlackboard
                    {
                        _ = bb.GetData<Pose>().X;
                        bb.SetData(bb.GetData<Decision>() with { Strike = true });
                        return Paradise.BT.NodeState.Success;
                    }
                }
            }
            """);

        await test.RunAsync().ConfigureAwait(false);
    }

    [Test]
    public async Task Handing_The_Blackboard_To_A_Helper_Is_Reported()
    {
        // Not followed, and said so rather than guessed at: attributing a helper's access to its
        // caller needs propagation along the call graph.
        var test = Test("""
            namespace Game
            {
                public struct DelegatingNode : Paradise.BT.INode
                {
                    private static void Helper<TBlackboard>(TBlackboard bb)
                        where TBlackboard : struct, Paradise.BT.IBlackboard
                        => bb.SetData(default(Decision));

                    public Paradise.BT.NodeState Tick<TBehaviorTree, TBlackboard>(
                        int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, Paradise.BT.IBehaviorTree
                        where TBlackboard : struct, Paradise.BT.IBlackboard
                    {
                        Helper(bb);
                        return Paradise.BT.NodeState.Success;
                    }
                }
            }
            """);

        // Only PBT0010. DelegatingNode declares nothing, so its own body is the generator's
        // source of truth and there is no declaration to contradict -- but the helper it calls is
        // still out of view, which is the whole point of the warning.
        test.ExpectedDiagnostics.Add(
            CSharpAnalyzerVerifier<BlackboardAccessAnalyzer, DefaultVerifier>
                .Diagnostic(BlackboardAccessAnalyzer.s_blackboardEscapes)
                .WithArguments("DelegatingNode", "Helper")
                .WithSpan(47, 13, 47, 23));

        await test.RunAsync().ConfigureAwait(false);
    }

    [Test]
    public async Task A_Non_Node_May_Use_A_Blackboard_Freely()
    {
        // Only nodes carry declarations; a system building or reading one is unconstrained.
        var test = Test("""
            namespace Game
            {
                public static class Host
                {
                    public static void Drive<TBlackboard>(TBlackboard bb)
                        where TBlackboard : struct, Paradise.BT.IBlackboard
                    {
                        _ = bb.GetData<Pose>();
                        bb.SetData(default(Decision));
                    }
                }
            }
            """);

        await test.RunAsync().ConfigureAwait(false);
    }
}
