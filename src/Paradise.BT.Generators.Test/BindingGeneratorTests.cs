using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Paradise.BT.Generators;

namespace Paradise.BT.Generators.Test;

/// <summary>
/// Driven through <see cref="CSharpGeneratorDriver"/> rather than the analyzer-testing harness,
/// for one reason worth the extra setup: it can COMPILE what the generator emitted. A generator
/// test that only inspects diagnostics proves the refusals work and says nothing about whether the
/// emitted blackboard — ref fields into chunk memory, handed out through an interface — is even
/// legal C#. <see cref="Binds_Components_And_Extras_And_The_Result_Compiles"/> is the test that
/// keeps the design honest.
/// </summary>
public sealed class BindingGeneratorTests
{
    /// <summary>
    /// Stand-ins for Paradise.BT and Paradise.ECS. The generator resolves both symbolically — it
    /// takes no reference on Paradise.ECS and could not, so a faithful stub is a complete
    /// substitute. <c>Segments</c> mirrors the real emitted shape exactly where it matters: a
    /// PROPERTY returning a ref struct by value, whose indexer returns a ref into chunk memory.
    /// That is the shape whose ref-safety was in question.
    /// </summary>
    private const string Prelude = """
        using System;

        namespace Paradise.BT
        {
            public enum NodeState { Success, Failure, Running }

            public interface INodeBlob { }

            public interface IBlackboard
            {
                bool HasData<T>() where T : struct;
                T GetData<T>() where T : struct;
                [System.Diagnostics.CodeAnalysis.UnscopedRef]
                ref T GetDataRef<T>() where T : struct;
            }

            public interface INodeData
            {
                NodeState Tick<TNodeBlob, TBlackboard>(int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
                    where TNodeBlob : struct, INodeBlob, allows ref struct
                    where TBlackboard : struct, IBlackboard, allows ref struct;
            }

            [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
            public sealed class ReadsAttribute<T> : Attribute where T : struct { }

            [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
            public sealed class WritesAttribute<T> : Attribute where T : struct { }

            [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
            public sealed class OptionalReadsAttribute<T> : Attribute where T : struct { }

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class BehaviorTreeBindingAttribute : Attribute
            {
                public BehaviorTreeBindingAttribute(Type queryable) => Queryable = queryable;
                public Type Queryable { get; }
                public Type[]? Also { get; set; }
            }
        }

        namespace Paradise.ECS
        {
            public interface IComponent { }

            [AttributeUsage(AttributeTargets.Struct)]
            public sealed class ComponentAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Struct)]
            public sealed class QueryableAttribute : Attribute { public bool Singleton { get; set; } }

            // `where T : struct`, not `unmanaged, IComponent` as the real one: in a real
            // compilation the ECS generator supplies `: IComponent`, and there is no generator
            // here to do it. Nothing in BindingGenerator reads this constraint.
            [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
            public sealed class WithAttribute<T> : Attribute where T : struct
            {
                public bool IsReadOnly { get; set; }
                public bool QueryOnly { get; set; }
                public string? Name { get; set; }
            }

            public readonly ref struct ComponentSegments<T> where T : struct
            {
                public ref T this[int index] => throw new NotImplementedException();
            }

            public readonly ref struct ReadOnlyComponentSegments<T> where T : struct
            {
                public ref readonly T this[int index] => throw new NotImplementedException();
            }
        }
        """;

    /// <summary>A queryable granting WorldTransform read-only and ChaseIntent writable, plus the
    /// hand-written Segments the real QueryableGenerator would emit.</summary>
    private const string World = """
        namespace Game
        {
            // Declared as ShiningPie declares them: the [Component] ATTRIBUTE and no interface,
            // because in a real build `: IComponent` comes from the ECS generator's output — which
            // this generator cannot see. Getting this stub wrong is exactly why the first version
            // of these tests passed while the real game put every component in the extras.
            [Paradise.ECS.Component] public struct WorldTransform { public float X; }
            [Paradise.ECS.Component] public struct ChaseIntent { public float X; }

            /// A component from a REFERENCED assembly: already compiled, so the interface is real
            /// metadata and the attribute may sit on a partial this compilation never sees.
            public struct Stunned : Paradise.ECS.IComponent { public bool Value; }

            /// A plain struct, NOT a component: it must land in the extras, not the row.
            public struct Decision { public bool Strike; }

            [Paradise.ECS.Queryable]
            [Paradise.ECS.With<WorldTransform>(IsReadOnly = true)]
            [Paradise.ECS.With<ChaseIntent>]
            public readonly ref partial struct Pack
            {
                public readonly ref struct Segments
                {
                    public Paradise.ECS.ReadOnlyComponentSegments<WorldTransform> WorldTransform
                        => throw new System.NotImplementedException();

                    public Paradise.ECS.ComponentSegments<ChaseIntent> ChaseIntent
                        => throw new System.NotImplementedException();
                }
            }
        }
        """;

    [Test]
    public async Task Binds_Components_And_Extras_And_The_Result_Compiles()
    {
        var (diagnostics, sources, compileErrors) = Run(Prelude + World + """
            namespace Game
            {
                [Paradise.BT.Reads<WorldTransform>]
                [Paradise.BT.Reads<ChaseIntent>]
                [Paradise.BT.Writes<Decision>]
                public struct SeekNode : Paradise.BT.INodeData
                {
                    public Paradise.BT.NodeState Tick<TNodeBlob, TBlackboard>(
                        int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
                        where TNodeBlob : struct, Paradise.BT.INodeBlob, allows ref struct
                        where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                    {
                        bb.GetDataRef<Decision>().Strike = true;
                        _ = bb.GetData<ChaseIntent>().X + bb.GetData<WorldTransform>().X;
                        return Paradise.BT.NodeState.Success;
                    }
                }

                [Paradise.BT.BehaviorTreeBinding(typeof(Pack))]
                public static class EnemyTree
                {
                    public static object Build() => new SeekNode();
                }
            }
            """);

        await Assert.That(diagnostics).IsEmpty();

        string generated = string.Join("\n", sources);

        // The component split. Components resolve off the row on demand — segments plus an index,
        // never a ref field, because a ref struct holding ref fields cannot be passed as `ref` to
        // the VM. The non-component lands in the extras struct rather than being sought on the row.
        await Assert.That(generated).Contains("private readonly global::Game.WorldTransform _worldTransform;");
        await Assert.That(generated).Contains("public global::Game.Decision Decision;");

        // Read-only reads the row by value; writable hands out a ref straight into the chunk.
        // A plain struct, NOT a ref struct. Load-bearing: a ref-struct blackboard cannot be passed
        // as `ref` to VirtualMachine.Tick, and with only value fields it buys nothing anyway.
        await Assert.That(generated).Contains("public struct EnemyTreeBlackboard");
        await Assert.That(generated).DoesNotContain("public ref struct EnemyTreeBlackboard");

        // A read-only claim must never yield a writable ref — that is the SingleWriter hole.
        await Assert.That(generated).Contains("is a component, bound BY VALUE");

        // The one that matters: what was emitted is legal C#, ref-safety included.
        await Assert.That(compileErrors).IsEmpty();
    }

    /// <summary>Component writes are refused wholesale while the blackboard binds by value —
    /// including ChaseIntent, which the queryable DOES grant writable. The claim is not the
    /// obstacle; getting a writable chunk reference into a node is.</summary>
    [Test]
    public async Task Writing_A_Component_Is_Refused_Even_When_The_Claim_Allows_It()
    {
        var (diagnostics, _, _) = Run(Prelude + World + """
            namespace Game
            {
                [Paradise.BT.Writes<ChaseIntent>]
                public struct ShoveNode : Paradise.BT.INodeData
                {
                    public Paradise.BT.NodeState Tick<TNodeBlob, TBlackboard>(
                        int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
                        where TNodeBlob : struct, Paradise.BT.INodeBlob, allows ref struct
                        where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                        => Paradise.BT.NodeState.Success;
                }

                [Paradise.BT.BehaviorTreeBinding(typeof(Pack))]
                public static class EnemyTree
                {
                    public static object Build() => new ShoveNode();
                }
            }
            """);

        await Assert.That(diagnostics.Select(d => d.Id)).Contains("PBT0008");
        await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .Contains("bound by value");
    }

    [Test]
    public async Task Reading_An_Unclaimed_Component_Is_Refused()
    {
        var (diagnostics, _, _) = Run(Prelude + World + """
            namespace Game
            {
                [Paradise.BT.Reads<Stunned>]
                public struct CheckNode : Paradise.BT.INodeData
                {
                    public Paradise.BT.NodeState Tick<TNodeBlob, TBlackboard>(
                        int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
                        where TNodeBlob : struct, Paradise.BT.INodeBlob, allows ref struct
                        where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                        => Paradise.BT.NodeState.Success;
                }

                [Paradise.BT.BehaviorTreeBinding(typeof(Pack))]
                public static class EnemyTree
                {
                    public static object Build() => new CheckNode();
                }
            }
            """);

        await Assert.That(diagnostics.Select(d => d.Id)).Contains("PBT0005");
        await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("does not claim it");
    }

    [Test]
    public async Task Optional_Access_Is_Refused_With_The_Reason()
    {
        var (diagnostics, _, _) = Run(Prelude + World + """
            namespace Game
            {
                [Paradise.BT.OptionalReads<Stunned>]
                public struct MaybeNode : Paradise.BT.INodeData
                {
                    public Paradise.BT.NodeState Tick<TNodeBlob, TBlackboard>(
                        int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
                        where TNodeBlob : struct, Paradise.BT.INodeBlob, allows ref struct
                        where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                        => Paradise.BT.NodeState.Success;
                }

                [Paradise.BT.BehaviorTreeBinding(typeof(Pack))]
                public static class EnemyTree
                {
                    public static object Build() => new MaybeNode();
                }
            }
            """);

        await Assert.That(diagnostics.Select(d => d.Id)).Contains("PBT0006");
        await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("Segments view");
    }

    [Test]
    public async Task Binding_A_Non_Queryable_Is_Refused()
    {
        var (diagnostics, _, _) = Run(Prelude + World + """
            namespace Game
            {
                public readonly ref struct NotAQueryable { }

                [Paradise.BT.BehaviorTreeBinding(typeof(NotAQueryable))]
                public static class EnemyTree
                {
                    public static object Build() => null!;
                }
            }
            """);

        await Assert.That(diagnostics.Select(d => d.Id)).Contains("PBT0007");
    }

    [Test]
    public async Task A_Tree_Whose_Nodes_Declare_Nothing_Emits_An_Empty_Binding()
    {
        var (diagnostics, sources, compileErrors) = Run(Prelude + World + """
            namespace Game
            {
                public struct PlainNode : Paradise.BT.INodeData
                {
                    public Paradise.BT.NodeState Tick<TNodeBlob, TBlackboard>(
                        int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
                        where TNodeBlob : struct, Paradise.BT.INodeBlob, allows ref struct
                        where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                        => Paradise.BT.NodeState.Success;
                }

                [Paradise.BT.BehaviorTreeBinding(typeof(Pack))]
                public static class EnemyTree
                {
                    public static object Build() => new PlainNode();
                }
            }
            """);

        // Structure-only nodes are the normal case and are not an error.
        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(compileErrors).IsEmpty();
        await Assert.That(string.Join("\n", sources)).Contains("This tree reads only components.");
    }

    /// <summary>
    /// A node a factory builds, so the tree never names it. <c>BuiltInBehaviorNodes.Delay</c> is
    /// the real case — <c>DelayTimerNode</c> is the one built-in that reads a blackboard, and
    /// missing it means the cooldown has no delta time and throws on its first tick.
    /// </summary>
    [Test]
    public async Task Also_Binds_A_Node_The_Tree_Never_Names()
    {
        var (diagnostics, sources, compileErrors) = Run(Prelude + World + """
            namespace Game
            {
                [Paradise.BT.Reads<Decision>]
                public struct TimerNode : Paradise.BT.INodeData
                {
                    public Paradise.BT.NodeState Tick<TNodeBlob, TBlackboard>(
                        int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
                        where TNodeBlob : struct, Paradise.BT.INodeBlob, allows ref struct
                        where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                        => Paradise.BT.NodeState.Success;
                }

                [Paradise.BT.BehaviorTreeBinding(typeof(Pack), Also = new[] { typeof(TimerNode) })]
                public static class EnemyTree
                {
                    // Deliberately never mentions TimerNode: a factory would have built it.
                    public static object Build() => null!;
                }
            }
            """);

        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(compileErrors).IsEmpty();
        await Assert.That(string.Join("\n", sources)).Contains("public global::Game.Decision Decision;");
    }

    // ===================== harness =====================

    private static (ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<string> Sources,
        ImmutableArray<Diagnostic> CompileErrors) Run(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "BindingGeneratorTestAssembly",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        // A stub that does not compile would make the generator find nothing and every assertion
        // fail for the wrong reason, so refuse it here with the compiler's own message.
        var inputErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
        if (inputErrors.Length > 0)
        {
            throw new InvalidOperationException(
                "The test source does not compile, so the generator was never given valid input:\n"
                + string.Join("\n", inputErrors.Select(d => d.ToString())));
        }

        GeneratorDriver driver = CSharpGeneratorDriver
            .Create(new BindingGenerator())
            .WithUpdatedParseOptions(parseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out Compilation output, out _);

        GeneratorDriverRunResult result = driver.GetRunResult();

        // Errors from the ORIGINAL source would mean the stub is broken, not the generator, so
        // only the generated trees are compiled for errors.
        var generatedTrees = output.SyntaxTrees.Except(compilation.SyntaxTrees).ToImmutableArray();
        var compileErrors = output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                && d.Location.SourceTree is not null
                && generatedTrees.Contains(d.Location.SourceTree))
            .ToImmutableArray();

        return (
            result.Diagnostics,
            result.Results.SelectMany(r => r.GeneratedSources)
                .Select(s => s.SourceText.ToString())
                .ToImmutableArray(),
            compileErrors);
    }
}
