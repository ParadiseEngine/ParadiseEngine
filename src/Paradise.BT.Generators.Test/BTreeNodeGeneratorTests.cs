using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Paradise.BT.Generators.Test;

/// <summary>
/// What the builder generator emits per cardinality. The composite case is the one with history:
/// composite fields used to be silently dropped, so a weighted selector could not be configured
/// through its builder at all — the constructor always wrote <c>new T()</c>.
/// </summary>
public sealed class BTreeNodeGeneratorTests
{
    /// <summary>Stand-ins for Paradise.BT and Paradise.BT.Builder, mirroring
    /// <see cref="BindingGeneratorTests"/>' approach: the generator resolves everything
    /// symbolically, and compiling its output needs the base classes the emitted builders derive
    /// from and the registry the emitted module initializer calls.</summary>
    private const string Prelude = """
        using System;

        namespace Paradise.BT
        {
            public enum NodeState { Success, Failure, Running }

            public interface INodeBlob { }

            public interface IBlackboard { }

            public interface INodeData
            {
                NodeState Tick<TNodeBlob, TBlackboard>(int index, TNodeBlob blob, TBlackboard bb)
                    where TNodeBlob : struct, INodeBlob, allows ref struct
                    where TBlackboard : struct, IBlackboard, allows ref struct;
            }

            public enum NodeCardinality { Leaf, Decorator, Composite }

            [AttributeUsage(AttributeTargets.Struct)]
            public sealed class BuilderAttribute : Attribute
            {
                public BuilderAttribute(NodeCardinality cardinality = NodeCardinality.Leaf) { }
                public BuilderAttribute(string name, NodeCardinality cardinality = NodeCardinality.Leaf) { }
            }

            public static class NodeTypeRegistry
            {
                public static int Register<T>() where T : unmanaged, INodeData => 0;
            }

            [AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method)]
            public sealed class RequireNamedArgumentsAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
            public sealed class NodeAccessAttribute : Attribute
            {
                public NodeAccessAttribute(Type node) { }
                public Type[]? Reads { get; set; }
                public Type[]? Writes { get; set; }
            }
        }

        namespace Paradise.BT.Builder
        {
            public abstract class BTreeNode { }

            public class LeafNode<T> : BTreeNode where T : struct, Paradise.BT.INodeData
            {
                public LeafNode(T data) { }
            }

            public class DecoratorNode<T> : BTreeNode where T : struct, Paradise.BT.INodeData
            {
                public DecoratorNode(T data, BTreeNode child) { }
            }

            public class CompositeNode<T> : BTreeNode where T : struct, Paradise.BT.INodeData
            {
                public CompositeNode(T data, params ReadOnlySpan<BTreeNode> children) { }
            }
        }
        """;

    [Test]
    public async Task A_Composite_Keeps_Its_Fields_In_The_Builder()
    {
        var (sources, compileErrors, _) = Run(
            """
            namespace Game;

            [System.Runtime.InteropServices.Guid("3E1A2B3C-4D5E-4F60-8172-93A4B5C6D7E8")]
            [Paradise.BT.Builder("Weighted", Paradise.BT.NodeCardinality.Composite)]
            public struct WeightedSelectorNode : Paradise.BT.INodeData
            {
                public int Seed;
                public float Bias;

                public Paradise.BT.NodeState Tick<TNodeBlob, TBlackboard>(
                    int index, TNodeBlob blob, TBlackboard bb)
                    where TNodeBlob : struct, Paradise.BT.INodeBlob, allows ref struct
                    where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                    => Paradise.BT.NodeState.Success;
            }
            """);

        await Assert.That(compileErrors).IsEmpty();

        string generated = string.Join("\n", sources);

        // Fields come first (all required — `params children` must be last), then the children.
        await Assert.That(generated).Contains(
            "public Weighted(int seed, float bias, "
            + "params global::System.ReadOnlySpan<global::Paradise.BT.Builder.BTreeNode> children)");
        await Assert.That(generated).Contains("Seed = seed");
        await Assert.That(generated).Contains("Bias = bias");
    }

    [Test]
    public async Task A_Composite_Without_Fields_Stays_Children_Only()
    {
        var (sources, compileErrors, _) = Run(
            """
            namespace Game;

            [System.Runtime.InteropServices.Guid("A1B2C3D4-E5F6-4708-9A0B-1C2D3E4F5A6B")]
            [Paradise.BT.Builder(Paradise.BT.NodeCardinality.Composite)]
            public struct AllNode : Paradise.BT.INodeData
            {
                public Paradise.BT.NodeState Tick<TNodeBlob, TBlackboard>(
                    int index, TNodeBlob blob, TBlackboard bb)
                    where TNodeBlob : struct, Paradise.BT.INodeBlob, allows ref struct
                    where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                    => Paradise.BT.NodeState.Success;
            }
            """);

        await Assert.That(compileErrors).IsEmpty();
        await Assert.That(string.Join("\n", sources)).Contains(
            "public All(params global::System.ReadOnlySpan<global::Paradise.BT.Builder.BTreeNode> children)");
    }

    [Test]
    public async Task A_Constructor_Defines_The_Exposed_Surface()
    {
        var (sources, compileErrors, _) = Run(
            """
            namespace Game;

            [System.Runtime.InteropServices.Guid("5F0A1B2C-3D4E-4F50-A162-73B4C5D6E7F8")]
            [Paradise.BT.Builder]
            public struct AimNode : Paradise.BT.INodeData
            {
                public float Speed;
                public int Retries;
                private float _progress;

                public AimNode(float speed, int retries = 3)
                {
                    Speed = speed;
                    Retries = retries;
                    _progress = 0f;
                }

                public Paradise.BT.NodeState Tick<TNodeBlob, TBlackboard>(
                    int index, TNodeBlob blob, TBlackboard bb)
                    where TNodeBlob : struct, Paradise.BT.INodeBlob, allows ref struct
                    where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                    => Paradise.BT.NodeState.Success;
            }
            """);

        await Assert.That(compileErrors).IsEmpty();

        string generated = string.Join("\n", sources);

        // Exposed = the constructor's parameters, declared defaults kept; construction goes
        // through the constructor so it may initialize the non-exposed state.
        await Assert.That(generated).Contains(
            "public Aim(float speed, int retries = 3) : base(new global::Game.AimNode(speed, retries)) { }");
        await Assert.That(generated).DoesNotContain("progress");
    }

    [Test]
    public async Task A_Decorator_Constructor_Orders_Required_Child_Optional()
    {
        var (sources, compileErrors, _) = Run(
            """
            namespace Game;

            [System.Runtime.InteropServices.Guid("6A1B2C3D-4E5F-4061-8273-94A5B6C7D8E9")]
            [Paradise.BT.Builder(Paradise.BT.NodeCardinality.Decorator)]
            public struct RetryNode : Paradise.BT.INodeData
            {
                public int Times;
                public Paradise.BT.NodeState Pass;

                public RetryNode(int times, Paradise.BT.NodeState pass = Paradise.BT.NodeState.Running)
                {
                    Times = times;
                    Pass = pass;
                }

                public Paradise.BT.NodeState Tick<TNodeBlob, TBlackboard>(
                    int index, TNodeBlob blob, TBlackboard bb)
                    where TNodeBlob : struct, Paradise.BT.INodeBlob, allows ref struct
                    where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                    => Paradise.BT.NodeState.Success;
            }
            """);

        await Assert.That(compileErrors).IsEmpty();

        // The enum default is re-rendered as a cast of the VALUE, which resolves in the
        // generated file no matter what was in scope at the declaration.
        await Assert.That(string.Join("\n", sources)).Contains(
            "public Retry(int times, global::Paradise.BT.Builder.BTreeNode child, "
            + "global::Paradise.BT.NodeState pass = (global::Paradise.BT.NodeState)(2)) "
            + ": base(new global::Game.RetryNode(times, pass), child) { }");
    }

    /// <summary>The short form: a primary constructor is the exposed surface too, whether its
    /// parameters initialize named fields or are captured directly.</summary>
    [Test]
    public async Task A_Primary_Constructor_Defines_The_Exposed_Surface()
    {
        var (sources, compileErrors, diagnostics) = Run(
            """
            namespace Game;

            [System.Runtime.InteropServices.Guid("9D4E5F60-7182-4394-A5B6-C7D8E9F0A1B2")]
            [Paradise.BT.Builder]
            public struct DashNode(float speed, int charges = 2) : Paradise.BT.INodeData
            {
                public float Speed = speed;
                public int Charges = charges;
                private float _cooldown = 0f;

                public Paradise.BT.NodeState Tick<TNodeBlob, TBlackboard>(
                    int index, TNodeBlob blob, TBlackboard bb)
                    where TNodeBlob : struct, Paradise.BT.INodeBlob, allows ref struct
                    where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                    => Paradise.BT.NodeState.Success;
            }
            """);

        await Assert.That(compileErrors).IsEmpty();
        await Assert.That(diagnostics.Where(d => d.Id.StartsWith("PBT", StringComparison.Ordinal))).IsEmpty();
        await Assert.That(string.Join("\n", sources)).Contains(
            "public Dash(float speed, int charges = 2) : base(new global::Game.DashNode(speed, charges)) { }");
    }

    /// <summary>Captured-parameter style: no named fields at all — the parameters ARE the state,
    /// stored in compiler-synthesized capture fields.</summary>
    [Test]
    public async Task A_Primary_Constructor_With_Captured_Parameters_Works()
    {
        var (sources, compileErrors, _) = Run(
            """
            namespace Game;

            [System.Runtime.InteropServices.Guid("AE5F6071-8293-44A5-B6C7-D8E9F0A1B2C3")]
            [Paradise.BT.Builder(Paradise.BT.NodeCardinality.Decorator)]
            public struct CooldownNode(float seconds) : Paradise.BT.INodeData
            {
                public Paradise.BT.NodeState Tick<TNodeBlob, TBlackboard>(
                    int index, TNodeBlob blob, TBlackboard bb)
                    where TNodeBlob : struct, Paradise.BT.INodeBlob, allows ref struct
                    where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                {
                    seconds -= 0.1f; // captured parameter is mutable node state
                    return seconds > 0f ? Paradise.BT.NodeState.Running : Paradise.BT.NodeState.Success;
                }
            }
            """);

        await Assert.That(compileErrors).IsEmpty();
        await Assert.That(string.Join("\n", sources)).Contains(
            "public Cooldown(float seconds, global::Paradise.BT.Builder.BTreeNode child) "
            + ": base(new global::Game.CooldownNode(seconds), child) { }");
    }

    /// <summary>A record struct's primary constructor is a declared public constructor too, so
    /// the same surface rules apply; its positional members are properties, not fields, so the
    /// field fallback and PBT0012 stay quiet.</summary>
    [Test]
    public async Task A_Record_Struct_Primary_Constructor_Works()
    {
        var (sources, compileErrors, diagnostics) = Run(
            """
            namespace Game;

            [System.Runtime.InteropServices.Guid("BF607182-93A4-45B6-C7D8-E9F0A1B2C3D4")]
            [Paradise.BT.Builder]
            public record struct PatrolNode(float Radius, int Waypoints = 4) : Paradise.BT.INodeData
            {
                public Paradise.BT.NodeState Tick<TNodeBlob, TBlackboard>(
                    int index, TNodeBlob blob, TBlackboard bb)
                    where TNodeBlob : struct, Paradise.BT.INodeBlob, allows ref struct
                    where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                    => Paradise.BT.NodeState.Success;
            }
            """);

        await Assert.That(compileErrors).IsEmpty();
        await Assert.That(diagnostics.Where(d => d.Id.StartsWith("PBT", StringComparison.Ordinal))).IsEmpty();
        await Assert.That(string.Join("\n", sources)).Contains(
            "public Patrol(float radius, int waypoints = 4) : base(new global::Game.PatrolNode(radius, waypoints)) { }");
    }

    [Test]
    public async Task Two_Public_Constructors_Are_Refused()
    {
        var (sources, _, diagnostics) = Run(
            """
            namespace Game;

            [System.Runtime.InteropServices.Guid("7B2C3D4E-5F60-4172-8394-A5B6C7D8E9F0")]
            [Paradise.BT.Builder]
            public struct TornNode : Paradise.BT.INodeData
            {
                public int A;

                public TornNode(int a) { A = a; }
                public TornNode(float a) { A = (int)a; }

                public Paradise.BT.NodeState Tick<TNodeBlob, TBlackboard>(
                    int index, TNodeBlob blob, TBlackboard bb)
                    where TNodeBlob : struct, Paradise.BT.INodeBlob, allows ref struct
                    where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                    => Paradise.BT.NodeState.Success;
            }
            """);

        await Assert.That(diagnostics.Select(d => d.Id)).Contains("PBT0011");
        await Assert.That(string.Join("\n", sources)).DoesNotContain("class Torn");
    }

    [Test]
    public async Task A_Public_Field_Outside_The_Constructor_Is_Flagged()
    {
        var (sources, _, diagnostics) = Run(
            """
            namespace Game;

            [System.Runtime.InteropServices.Guid("8C3D4E5F-6071-4283-94A5-B6C7D8E9F0A1")]
            [Paradise.BT.Builder]
            public struct LeakyNode : Paradise.BT.INodeData
            {
                public float Speed;
                public float Elapsed; // runtime state, but public and not a constructor parameter

                public LeakyNode(float speed) { Speed = speed; Elapsed = 0f; }

                public Paradise.BT.NodeState Tick<TNodeBlob, TBlackboard>(
                    int index, TNodeBlob blob, TBlackboard bb)
                    where TNodeBlob : struct, Paradise.BT.INodeBlob, allows ref struct
                    where TBlackboard : struct, Paradise.BT.IBlackboard, allows ref struct
                    => Paradise.BT.NodeState.Success;
            }
            """);

        await Assert.That(diagnostics.Select(d => d.Id)).Contains("PBT0012");

        // Flagged, not fatal: the builder is still generated, without the stray field.
        string generated = string.Join("\n", sources);
        await Assert.That(generated).Contains("public Leaky(float speed)");
        await Assert.That(generated).DoesNotContain("elapsed");
    }

    // ===================== harness =====================

    private static (ImmutableArray<string> Sources, ImmutableArray<Diagnostic> CompileErrors,
        ImmutableArray<Diagnostic> Diagnostics) Run(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "BTreeNodeGeneratorTestAssembly",
            [
                CSharpSyntaxTree.ParseText(Prelude, parseOptions),
                CSharpSyntaxTree.ParseText(source, parseOptions),
            ],
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

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
            .Create(new BTreeNodeGenerator())
            .WithUpdatedParseOptions(parseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out Compilation output, out _);

        GeneratorDriverRunResult result = driver.GetRunResult();

        var generatedTrees = output.SyntaxTrees.Except(compilation.SyntaxTrees).ToImmutableArray();
        var compileErrors = output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                && d.Location.SourceTree is not null
                && generatedTrees.Contains(d.Location.SourceTree))
            .ToImmutableArray();

        return (
            result.Results.SelectMany(r => r.GeneratedSources)
                .Select(s => s.SourceText.ToString())
                .ToImmutableArray(),
            compileErrors,
            result.Diagnostics);
    }
}
