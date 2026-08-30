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

        // The component split. Components resolve off the row on demand — segments plus an index,
        // never a ref field, because a ref struct holding ref fields cannot be passed as `ref` to
        // the VM. The non-component lands in the extras struct rather than being sought on the row.
        await Assert.That(generated).Contains("ref readonly global::Game.WorldTransform _worldTransform;");
        await Assert.That(generated).Contains("ref global::Game.Decision decision");

        // Read-only reads the row by value; writable hands out a ref straight into the chunk.
        // A ref struct, holding a reference to everything it touches. Passable because the VM
        // takes a blackboard BY VALUE; by `ref` this shape is rejected outright.
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

    /// <summary>
    /// A node a factory builds, so the tree never names it. The escape hatch of last resort, for a
    /// factory carrying no [Builds&lt;T&gt;]; prefer annotating the factory. The attribute is now
    /// ONLY this: the interface marks the tree, [BehaviorTreeBinding] just carries Also.
    /// </summary>
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

    /// <summary>
    /// A node that declares NOTHING still contributes, because its body is read directly. This is
    /// only decidable since GetData/SetData replaced the ref-returning accessor: taking a ref to
    /// avoid a copy and taking one to mutate were the same call.
    ///
    /// The declarations remain the cross-assembly contract — a node from a referenced assembly has
    /// no body to read — so the two are unioned rather than one replacing the other.
    /// </summary>
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

    /// <summary>
    /// A tree written with the builder DSL binds too. A builder derives from
    /// <c>CompositeNode&lt;T&gt;</c> and friends, so the node type survives as a generic argument
    /// on the base — the tree's source never says <c>HiddenNode</c>, and the scan finds it anyway.
    ///
    /// This is what separates the DSL from a factory method: a method returning
    /// <c>BehaviorNodeDefinition</c> discards the type and has to be told.
    /// </summary>
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

    /// <summary>
    /// A builder generated BESIDE the tree, in the same compilation. BTreeNodeGenerator would emit
    /// <c>Hidden</c> for <c>HiddenNode</c>, but this generator cannot see another generator's
    /// output, so the reference is an error type at the moment the scan runs.
    ///
    /// Recovered by NAME against a table of the builders that will be emitted, derived from the
    /// same <c>[Builder]</c> declarations. Without it, a tree composed of its own assembly's
    /// builders binds an empty blackboard — compiling cleanly and failing on the first tick.
    /// </summary>
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

    /// <summary>
    /// A factory RETURNING a builder is transparent, which is what lets the DSL drop <c>new</c>.
    ///
    /// The distinction is the return type and nothing else. The factories deleted from this library
    /// returned a bare <c>BehaviorNodeDefinition</c> and discarded every trace of what they built;
    /// a method typed <c>Hidden</c> still carries <c>HiddenNode</c> on that type's base, so the
    /// scan follows it with no annotation at all.
    /// </summary>
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

    /// <summary>Two same-named data types both survive the FQN-keyed merge, so their generated
    /// identifiers must be disambiguated — two `_target` fields would be CS0102 in a file the
    /// user cannot edit. A type named after a keyword must be escaped for the same reason.</summary>
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

    /// <summary>
    /// The cross-assembly path with NO hand-written declarations: the node's declaring assembly is
    /// compiled separately (its BODY does not survive into metadata), BTreeNodeGenerator publishes
    /// the body-scanned access as <c>[assembly: NodeAccess]</c>, and this binding reads it off the
    /// metadata reference — which is what made attributes like DelayTimerNode's optional.
    /// </summary>
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

    // ===================== harness =====================

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

        // A stub that does not compile would make the generator find nothing and every assertion
        // fail for the wrong reason, so refuse it here with the compiler's own message.
        // CS0246 is EXPECTED where a test composes a tree from builders another generator would
        // emit: they do not exist at this point, which is exactly the case being exercised.
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
}
