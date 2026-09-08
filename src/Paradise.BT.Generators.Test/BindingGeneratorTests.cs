using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Paradise.BT.Generators;

namespace Paradise.BT.Generators.Test;

/// <summary>Compiles generated blackboards with CSharpGeneratorDriver to verify emitted code and ref safety.</summary>
public sealed class BindingGeneratorTests
{
    /// <summary>Stubs BT/ECS symbols, including a Segments property whose indexer returns a chunk reference.</summary>
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

            public interface IBlackboardFor<TTree> : IBlackboard { }

            public interface INode
            {
                NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
                    where TBehaviorTree : struct, IBehaviorTree, allows ref struct
                    where TBlackboard : struct, IBlackboard, allows ref struct;
            }

            [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
            public sealed class ReadsAttribute<T> : Attribute where T : struct { }

            [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
            public sealed class WritesAttribute<T> : Attribute where T : struct { }

            [AttributeUsage(AttributeTargets.Struct)]
            public sealed class BuilderAttribute : Attribute
            {
                public BuilderAttribute(string? name = null) { }
            }

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
            public sealed class BehaviorTreeBindingAttribute : Attribute
            {
                public Type[]? Also { get; set; }
            }
        }

        namespace Paradise.BT.Builder
        {
            public abstract class BTreeNode { }

            public class LeafNode<T> : BTreeNode where T : struct, Paradise.BT.INode
            {
                public LeafNode() { }
                public LeafNode(T data) { }
            }

            public interface IBehaviorTreeBuilder
            {
                static abstract BTreeNode Build();
            }

            public interface IBehaviorTreeBuilder<TArgs>
            {
                static abstract BTreeNode Build(TArgs args);
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
                public struct SeekNode : Paradise.BT.INode
                {
                    public Paradise.BT.NodeState Tick<TBehaviorTree, TBlackboard>(
                        int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, Paradise.BT.IBehaviorTree, allows ref struct
                        where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                    {
                        bb.SetData(bb.GetData<Decision>() with { Strike = true });
                        _ = bb.GetData<ChaseIntent>().X + bb.GetData<WorldTransform>().X;
                        return Paradise.BT.NodeState.Success;
                    }
                }

                public struct EnemyTree : Paradise.BT.Builder.IBehaviorTreeBuilder
                {
                    public static Paradise.BT.Builder.BTreeNode Build() { _ = new SeekNode(); return null!; }
                }
            }
            """);

        await Assert.That(diagnostics).IsEmpty();

        string generated = string.Join("\n", sources);

        // Components bind read-only; writable non-components bind directly to caller storage.
        await Assert.That(generated).Contains("ref readonly global::Game.WorldTransform _worldTransform;");
        await Assert.That(generated).Contains("ref global::Game.Decision decision");

        // The VM accepts the ref-struct blackboard by value to satisfy ref-safety rules.
        await Assert.That(generated).Contains("public readonly ref struct EnemyTreeBlackboard");

        // Read-only access is held by `ref readonly`, so SetData on it has nowhere to go.
        await Assert.That(generated).Contains("is bound read-only");

        // The one that matters: what was emitted is legal C#, ref-safety included.
        await Assert.That(compileErrors).IsEmpty();
    }

    /// <summary>Components bind read-only by value — there is no claim to write through, so a
    /// component write is refused outright. The tree writes conclusions the caller applies.</summary>
    [Test]
    public async Task Writing_A_Component_Is_Refused()
    {
        var (diagnostics, _, _) = Run(Prelude + World + """
            namespace Game
            {
                [Paradise.BT.Writes<WorldTransform>]
                public struct ShoveNode : Paradise.BT.INode
                {
                    public Paradise.BT.NodeState Tick<TBehaviorTree, TBlackboard>(
                        int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, Paradise.BT.IBehaviorTree, allows ref struct
                        where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                        => Paradise.BT.NodeState.Success;
                }

                public struct EnemyTree : Paradise.BT.Builder.IBehaviorTreeBuilder
                {
                    public static Paradise.BT.Builder.BTreeNode Build() { _ = new ShoveNode(); return null!; }
                }
            }
            """);

        await Assert.That(diagnostics.Select(d => d.Id)).Contains("PBT0008");
        await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .Contains("read-only by value");
    }

    /// <summary>There is no claims list to be absent from: any component a node reads simply
    /// binds read-only. The union of the nodes' access IS the contract.</summary>
    [Test]
    public async Task Reading_Any_Component_Binds_It_Read_Only()
    {
        var (diagnostics, sources, compileErrors) = Run(Prelude + World + """
            namespace Game
            {
                [Paradise.BT.Reads<Stunned>]
                public struct CheckNode : Paradise.BT.INode
                {
                    public Paradise.BT.NodeState Tick<TBehaviorTree, TBlackboard>(
                        int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, Paradise.BT.IBehaviorTree, allows ref struct
                        where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                        => Paradise.BT.NodeState.Success;
                }

                public struct EnemyTree : Paradise.BT.Builder.IBehaviorTreeBuilder
                {
                    public static Paradise.BT.Builder.BTreeNode Build() { _ = new CheckNode(); return null!; }
                }
            }
            """);

        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(compileErrors).IsEmpty();
        await Assert.That(string.Join("\n", sources))
            .Contains("ref readonly global::Game.Stunned");
    }

    [Test]
    public async Task A_Tree_Whose_Nodes_Declare_Nothing_Emits_An_Empty_Binding()
    {
        var (diagnostics, sources, compileErrors) = Run(Prelude + World + """
            namespace Game
            {
                public struct PlainNode : Paradise.BT.INode
                {
                    public Paradise.BT.NodeState Tick<TBehaviorTree, TBlackboard>(
                        int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, Paradise.BT.IBehaviorTree, allows ref struct
                        where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                        => Paradise.BT.NodeState.Success;
                }

                public struct EnemyTree : Paradise.BT.Builder.IBehaviorTreeBuilder
                {
                    public static Paradise.BT.Builder.BTreeNode Build() { _ = new PlainNode(); return null!; }
                }
            }
            """);

        // Structure-only nodes are the normal case and are not an error.
        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(compileErrors).IsEmpty();
        await Assert.That(string.Join("\n", sources)).Contains("This tree touches nothing");
    }

    /// <summary>Uses BehaviorTreeBinding.Also to include a node hidden behind a factory return type.</summary>
    [Test]
    public async Task Also_Binds_A_Node_The_Tree_Never_Names()
    {
        var (diagnostics, sources, compileErrors) = Run(Prelude + World + """
            namespace Game
            {
                [Paradise.BT.Reads<Decision>]
                public struct TimerNode : Paradise.BT.INode
                {
                    public Paradise.BT.NodeState Tick<TBehaviorTree, TBlackboard>(
                        int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, Paradise.BT.IBehaviorTree, allows ref struct
                        where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                        => Paradise.BT.NodeState.Success;
                }

                [Paradise.BT.BehaviorTreeBinding(Also = new[] { typeof(TimerNode) })]
                public struct EnemyTree : Paradise.BT.Builder.IBehaviorTreeBuilder
                {
                    // Deliberately never mentions TimerNode: a factory would have built it.
                    public static Paradise.BT.Builder.BTreeNode Build() => null!;
                }
            }
            """);

        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(compileErrors).IsEmpty();

        // TimerNode only READS Decision, so it arrives as a Bind parameter rather than an extra.
        await Assert.That(string.Join("\n", sources)).Contains("in global::Game.Decision decision");
    }

    /// <summary>Collects undeclared access from local node bodies and unions it with metadata declarations.</summary>
    [Test]
    public async Task A_Node_That_Declares_Nothing_Is_Read_From_Its_Body()
    {
        var (diagnostics, sources, compileErrors) = Run(Prelude + World + """
            namespace Game
            {
                public struct SilentNode : Paradise.BT.INode
                {
                    public Paradise.BT.NodeState Tick<TBehaviorTree, TBlackboard>(
                        int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, Paradise.BT.IBehaviorTree, allows ref struct
                        where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                    {
                        // Never declared, only performed.
                        _ = bb.GetData<WorldTransform>().X;
                        bb.SetData(bb.GetData<Decision>() with { Strike = true });
                        return Paradise.BT.NodeState.Success;
                    }
                }

                public struct EnemyTree : Paradise.BT.Builder.IBehaviorTreeBuilder
                {
                    public static Paradise.BT.Builder.BTreeNode Build() { _ = new SilentNode(); return null!; }
                }
            }
            """);

        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(compileErrors).IsEmpty();

        string generated = string.Join("\n", sources);
        await Assert.That(generated).Contains("ref readonly global::Game.WorldTransform _worldTransform;");
        await Assert.That(generated).Contains("ref global::Game.Decision decision");
    }

    /// <summary>
    /// The scan carries the same weight as a declaration: a component write performed only in the
    /// BODY is checked against the claim exactly as a declared one is.
    /// </summary>
    [Test]
    public async Task A_Component_Write_Is_Checked_Even_When_Only_The_Body_Says_So()
    {
        var (diagnostics, _, _) = Run(Prelude + World + """
            namespace Game
            {
                public struct ShoveNode : Paradise.BT.INode
                {
                    public Paradise.BT.NodeState Tick<TBehaviorTree, TBlackboard>(
                        int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, Paradise.BT.IBehaviorTree, allows ref struct
                        where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                    {
                        bb.SetData(bb.GetData<WorldTransform>() with { X = 1f });
                        return Paradise.BT.NodeState.Success;
                    }
                }

                public struct EnemyTree : Paradise.BT.Builder.IBehaviorTreeBuilder
                {
                    public static Paradise.BT.Builder.BTreeNode Build() { _ = new ShoveNode(); return null!; }
                }
            }
            """);

        await Assert.That(diagnostics.Select(d => d.Id)).Contains("PBT0008");
    }

    /// <summary>Finds a DSL node through its builder's generic base type.</summary>
    [Test]
    public async Task A_Tree_Built_With_The_Builder_Dsl_Binds()
    {
        var (diagnostics, sources, compileErrors) = Run(Prelude + World + """
            namespace Game
            {
                public struct HiddenNode : Paradise.BT.INode
                {
                    public Paradise.BT.NodeState Tick<TBehaviorTree, TBlackboard>(
                        int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, Paradise.BT.IBehaviorTree, allows ref struct
                        where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                    {
                        bb.SetData(bb.GetData<Decision>() with { Strike = true });
                        return Paradise.BT.NodeState.Success;
                    }
                }

                /// The generated builder shape: the node is a type argument on the base.
                public sealed class Hidden : Paradise.BT.Builder.LeafNode<HiddenNode> { }

                public struct EnemyTree : Paradise.BT.Builder.IBehaviorTreeBuilder
                {
                    public static Paradise.BT.Builder.BTreeNode Build() { _ = new Hidden(); return null!; }
                }
            }
            """);

        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(compileErrors).IsEmpty();
        await Assert.That(string.Join("\n", sources)).Contains("ref global::Game.Decision decision");
    }

    /// <summary>Resolves builders awaiting generation by name from their Builder declarations.</summary>
    /// <remarks>Generators cannot see each other's output in the same compilation.</remarks>
    [Test]
    public async Task A_Builder_Generated_Beside_The_Tree_Is_Recovered_By_Name()
    {
        var (diagnostics, sources, _) = Run(expectUnresolvedNames: true, source: Prelude + World + """
            namespace Game
            {
                [Paradise.BT.Builder]
                public struct HiddenNode : Paradise.BT.INode
                {
                    public Paradise.BT.NodeState Tick<TBehaviorTree, TBlackboard>(
                        int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, Paradise.BT.IBehaviorTree, allows ref struct
                        where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                    {
                        bb.SetData(bb.GetData<Decision>() with { Strike = true });
                        return Paradise.BT.NodeState.Success;
                    }
                }

                public struct EnemyTree : Paradise.BT.Builder.IBehaviorTreeBuilder
                {
                    // `Hidden` does not exist yet — the other generator emits it.
                    public static Paradise.BT.Builder.BTreeNode Build() { _ = new Hidden(); return null!; }
                }
            }
            """);

        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(string.Join("\n", sources)).Contains("ref global::Game.Decision decision");
    }

    /// <summary>Finds factory-created nodes through concrete builder return types without annotations.</summary>
    [Test]
    public async Task A_Factory_Returning_A_Builder_Needs_No_Annotation()
    {
        var (diagnostics, sources, compileErrors) = Run(Prelude + World + """
            namespace Game
            {
                public struct HiddenNode : Paradise.BT.INode
                {
                    public Paradise.BT.NodeState Tick<TBehaviorTree, TBlackboard>(
                        int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, Paradise.BT.IBehaviorTree, allows ref struct
                        where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                    {
                        bb.SetData(bb.GetData<Decision>() with { Strike = true });
                        return Paradise.BT.NodeState.Success;
                    }
                }

                public sealed class Hidden : Paradise.BT.Builder.LeafNode<HiddenNode> { }

                public static class Nodes
                {
                    // No [Builds<T>]: the return type already says it.
                    public static Hidden Hidden() => new();
                }

                public struct EnemyTree : Paradise.BT.Builder.IBehaviorTreeBuilder
                {
                    public static Paradise.BT.Builder.BTreeNode Build() => Nodes.Hidden();
                }
            }
            """);

        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(compileErrors).IsEmpty();
        await Assert.That(string.Join("\n", sources)).Contains("ref global::Game.Decision decision");
    }

    /// <summary>Disambiguates same-named data types and escapes keyword identifiers in generated code.</summary>
    [Test]
    public async Task Colliding_And_Keyword_Type_Names_Still_Compile()
    {
        var (diagnostics, sources, compileErrors) = Run(Prelude + World + """
            namespace North { public struct Target { public float X; } }
            namespace South { public struct Target { public float X; } }
            namespace Game
            {
                public struct Event { public int Id; }

                public struct BusyNode : Paradise.BT.INode
                {
                    public Paradise.BT.NodeState Tick<TBehaviorTree, TBlackboard>(
                        int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, Paradise.BT.IBehaviorTree, allows ref struct
                        where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                    {
                        bb.SetData(bb.GetData<North.Target>());
                        bb.SetData(bb.GetData<South.Target>());
                        bb.SetData(bb.GetData<Event>());
                        return Paradise.BT.NodeState.Success;
                    }
                }

                public struct EnemyTree : Paradise.BT.Builder.IBehaviorTreeBuilder
                {
                    public static Paradise.BT.Builder.BTreeNode Build() { _ = new BusyNode(); return null!; }
                }
            }
            """);

        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(compileErrors).IsEmpty();

        string generated = string.Join("\n", sources);
        await Assert.That(generated).Contains("_target_North");
        await Assert.That(generated).Contains("_target_South");
        await Assert.That(generated).Contains("@event");
    }

    /// <summary>Binding hint names are namespace-qualified too: two same-named tree types in
    /// different namespaces must both emit rather than killing the generator.</summary>
    [Test]
    public async Task Same_Named_Trees_In_Two_Namespaces_Both_Emit()
    {
        var (diagnostics, sources, compileErrors) = Run(Prelude + World + """
            namespace North
            {
                public struct EnemyTree : Paradise.BT.Builder.IBehaviorTreeBuilder
                {
                    public static Paradise.BT.Builder.BTreeNode Build() => null!;
                }
            }

            namespace South
            {
                public struct EnemyTree : Paradise.BT.Builder.IBehaviorTreeBuilder
                {
                    public static Paradise.BT.Builder.BTreeNode Build() => null!;
                }
            }
            """);

        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(compileErrors).IsEmpty();
        await Assert.That(sources.Length).IsEqualTo(2);
    }

    /// <summary>Reads body-scanned NodeAccess metadata from a separately compiled node assembly.</summary>
    [Test]
    public async Task A_Referenced_Nodes_Access_Arrives_Through_Generated_Metadata()
    {
        const string nodesSource = """

            namespace Paradise.BT
            {
                [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
                public sealed class NodeAccessAttribute : Attribute
                {
                    public NodeAccessAttribute(Type node) { }
                    public Type[]? Reads { get; set; }
                    public Type[]? Writes { get; set; }
                }

                [AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method)]
                public sealed class RequireNamedArgumentsAttribute : Attribute { }

                public static class NodeTypeRegistry
                {
                    public static int Register<T>() where T : unmanaged, INode => 0;
                }
            }

            namespace Game
            {
                /// An extra, not a component — read only by ClockNode's BODY, in this assembly.
                public struct Pulse { public int Ticks; }

                [System.Runtime.InteropServices.Guid("C0FFEE00-1234-4ABC-8DEF-000000000001")]
                [Paradise.BT.Builder]
                public struct ClockNode : Paradise.BT.INode
                {
                    public Paradise.BT.NodeState Tick<TBehaviorTree, TBlackboard>(
                        int index, TBehaviorTree blob, TBlackboard bb)
                        where TBehaviorTree : struct, Paradise.BT.IBehaviorTree, allows ref struct
                        where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                        => bb.GetData<Pulse>().Ticks > 0
                            ? Paradise.BT.NodeState.Success
                            : Paradise.BT.NodeState.Failure;
                }
            }
            """;

        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var runtimeReferences = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToImmutableArray();
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true);

        var nodesCompilation = CSharpCompilation.Create(
            "ExternalNodesAssembly",
            [CSharpSyntaxTree.ParseText(Prelude + nodesSource, parseOptions)],
            runtimeReferences,
            options);

        CSharpGeneratorDriver.Create(new BTreeNodeGenerator())
            .WithUpdatedParseOptions(parseOptions)
            .RunGeneratorsAndUpdateCompilation(nodesCompilation, out Compilation nodesOutput, out _);

        using var image = new System.IO.MemoryStream();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = nodesOutput.Emit(image);
        await Assert.That(emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(emitted.Success).IsTrue();

        const string treeSource = """
            namespace Game
            {
                public struct ClockTree : Paradise.BT.Builder.IBehaviorTreeBuilder
                {
                    public static Paradise.BT.Builder.BTreeNode Build() => new Game.Builder.Clock();
                }
            }
            """;

        var treeCompilation = CSharpCompilation.Create(
            "TreeAssembly",
            [CSharpSyntaxTree.ParseText(treeSource, parseOptions)],
            runtimeReferences.Add(MetadataReference.CreateFromImage(image.ToArray())),
            options);

        var inputErrors = treeCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
        await Assert.That(inputErrors).IsEmpty();

        GeneratorDriver driver = CSharpGeneratorDriver
            .Create(new BindingGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            treeCompilation, out Compilation treeOutput, out _);
        GeneratorDriverRunResult result = driver.GetRunResult();

        await Assert.That(result.Diagnostics).IsEmpty();

        var generatedTrees = treeOutput.SyntaxTrees.Except(treeCompilation.SyntaxTrees).ToImmutableArray();
        var compileErrors = treeOutput.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                && d.Location.SourceTree is not null
                && generatedTrees.Contains(d.Location.SourceTree))
            .ToImmutableArray();
        await Assert.That(compileErrors).IsEmpty();

        // Pulse reached the blackboard although no declaration for it exists anywhere in source.
        await Assert.That(string.Join("\n", result.Results
                .SelectMany(r => r.GeneratedSources)
                .Select(s => s.SourceText.ToString())))
            .Contains("global::Game.Pulse");
    }

    // harness

    private static (ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<string> Sources,
        ImmutableArray<Diagnostic> CompileErrors) Run(string source, bool expectUnresolvedNames = false)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "BindingGeneratorTestAssembly",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        // Reject invalid stubs, except CS0246 for builders awaiting another generator's output.
        var inputErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                && !(expectUnresolvedNames && d.Id == "CS0246"))
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

    /// <summary>Uses the tree symbol's full name so nested trees remain valid IBlackboardFor arguments.</summary>
    [Test]
    public async Task A_Nested_Tree_Gets_A_Correctly_Qualified_Blackboard_Marker()
    {
        var (diagnostics, sources, compileErrors) = Run(Prelude + World + """
            namespace Game
            {
                public static class Outer
                {
                    public struct InnerTree : Paradise.BT.Builder.IBehaviorTreeBuilder
                    {
                        public static Paradise.BT.Builder.BTreeNode Build() => null!;
                    }
                }
            }
            """);

        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(compileErrors).IsEmpty();
        await Assert.That(string.Join("\n", sources))
            .Contains("IBlackboardFor<global::Game.Outer.InnerTree>");
    }

}
