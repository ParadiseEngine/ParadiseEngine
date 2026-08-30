using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Paradise.BT.Generators;

namespace Paradise.BT.Generators.Test;

public class DuplicateGuidAnalyzerTests
{
    private static DiagnosticResult Diagnostic(params object[] arguments)
    {
        return CSharpAnalyzerVerifier<DuplicateGuidAnalyzer, DefaultVerifier>
            .Diagnostic(DuplicateGuidAnalyzer.s_duplicateGuidDiagnostic)
            .WithArguments(arguments);
    }

    [Test]
    public async Task DuplicateGuids_OnINodeStructs_ReportsError()
    {
        const string testCode = """
            using System.Runtime.InteropServices;

            namespace Paradise.BT
            {
                public enum NodeState { Success, Failure, Running }
                public interface IBehaviorTree { }
                public interface IBlackboard { }
                public interface INode
                {
                    NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, IBehaviorTree
                        where TBlackboard : struct, IBlackboard;
                }

                [Guid("11111111-1111-1111-1111-111111111111")]
                public struct NodeA : INode
                {
                    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, IBehaviorTree
                        where TBlackboard : struct, IBlackboard
                    {
                        return NodeState.Success;
                    }
                }

                [Guid("11111111-1111-1111-1111-111111111111")]
                public struct NodeB : INode
                {
                    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, IBehaviorTree
                        where TBlackboard : struct, IBlackboard
                    {
                        return NodeState.Success;
                    }
                }
            }
            """;

        var test = new CSharpAnalyzerTest<DuplicateGuidAnalyzer, DefaultVerifier>
        {
            TestCode = testCode,
        };

        test.ExpectedDiagnostics.Add(
            Diagnostic("11111111-1111-1111-1111-111111111111", "Paradise.BT.NodeA", "Paradise.BT.NodeB")
                .WithSpan(16, 19, 16, 24));
        test.ExpectedDiagnostics.Add(
            Diagnostic("11111111-1111-1111-1111-111111111111", "Paradise.BT.NodeB", "Paradise.BT.NodeA")
                .WithSpan(27, 19, 27, 24));

        await test.RunAsync().ConfigureAwait(false);
    }

    [Test]
    public async Task UniqueGuids_OnINodeStructs_NoDiagnostic()
    {
        const string testCode = """
            using System.Runtime.InteropServices;

            namespace Paradise.BT
            {
                public enum NodeState { Success, Failure, Running }
                public interface IBehaviorTree { }
                public interface IBlackboard { }
                public interface INode
                {
                    NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, IBehaviorTree
                        where TBlackboard : struct, IBlackboard;
                }

                [Guid("11111111-1111-1111-1111-111111111111")]
                public struct NodeA : INode
                {
                    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, IBehaviorTree
                        where TBlackboard : struct, IBlackboard
                    {
                        return NodeState.Success;
                    }
                }

                [Guid("22222222-2222-2222-2222-222222222222")]
                public struct NodeB : INode
                {
                    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, IBehaviorTree
                        where TBlackboard : struct, IBlackboard
                    {
                        return NodeState.Success;
                    }
                }
            }
            """;

        var test = new CSharpAnalyzerTest<DuplicateGuidAnalyzer, DefaultVerifier>
        {
            TestCode = testCode,
        };

        await test.RunAsync().ConfigureAwait(false);
    }

    [Test]
    public async Task DuplicateGuids_OnNonINodeStructs_NoDiagnostic()
    {
        const string testCode = """
            using System.Runtime.InteropServices;

            namespace Paradise.BT
            {
                public enum NodeState { Success, Failure, Running }
                public interface IBehaviorTree { }
                public interface IBlackboard { }
                public interface INode
                {
                    NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, IBehaviorTree
                        where TBlackboard : struct, IBlackboard;
                }
            }

            namespace Other
            {
                [Guid("11111111-1111-1111-1111-111111111111")]
                public struct NotANode
                {
                    public int Value;
                }

                [Guid("11111111-1111-1111-1111-111111111111")]
                public struct AlsoNotANode
                {
                    public int Value;
                }
            }
            """;

        var test = new CSharpAnalyzerTest<DuplicateGuidAnalyzer, DefaultVerifier>
        {
            TestCode = testCode,
        };

        await test.RunAsync().ConfigureAwait(false);
    }

    [Test]
    public async Task DuplicateGuids_CaseInsensitive_ReportsError()
    {
        const string testCode = """
            using System.Runtime.InteropServices;

            namespace Paradise.BT
            {
                public enum NodeState { Success, Failure, Running }
                public interface IBehaviorTree { }
                public interface IBlackboard { }
                public interface INode
                {
                    NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, IBehaviorTree
                        where TBlackboard : struct, IBlackboard;
                }

                [Guid("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA")]
                public struct NodeA : INode
                {
                    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, IBehaviorTree
                        where TBlackboard : struct, IBlackboard
                    {
                        return NodeState.Success;
                    }
                }

                [Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
                public struct NodeB : INode
                {
                    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, IBehaviorTree
                        where TBlackboard : struct, IBlackboard
                    {
                        return NodeState.Success;
                    }
                }
            }
            """;

        var test = new CSharpAnalyzerTest<DuplicateGuidAnalyzer, DefaultVerifier>
        {
            TestCode = testCode,
        };

        test.ExpectedDiagnostics.Add(
            Diagnostic("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA", "Paradise.BT.NodeA", "Paradise.BT.NodeB")
                .WithSpan(16, 19, 16, 24));
        test.ExpectedDiagnostics.Add(
            Diagnostic("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Paradise.BT.NodeB", "Paradise.BT.NodeA")
                .WithSpan(27, 19, 27, 24));

        await test.RunAsync().ConfigureAwait(false);
    }
}
