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
        var (sources, compileErrors) = Run(
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
        var (sources, compileErrors) = Run(
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

    // ===================== harness =====================

    private static (ImmutableArray<string> Sources, ImmutableArray<Diagnostic> CompileErrors)
        Run(string source)
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
            compileErrors);
    }
}
