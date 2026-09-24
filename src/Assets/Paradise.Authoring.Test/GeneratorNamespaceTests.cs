using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Paradise.Authoring.Generators;

namespace Paradise.Authoring.Test;

/// <summary>
/// Where the generated <c>AuthoringSchema</c> class LANDS.
///
/// This assembly cannot test that by itself: its RootNamespace and its assembly name are the same
/// string, so both candidate rules produce the same answer. A project where they differ — the
/// common `Game.Core` assembly with a `Game` root namespace — is exactly the case that broke, so
/// the generator is driven directly over a synthetic compilation instead.
/// </summary>
public class GeneratorNamespaceTests
{
    private const string Source = """
        using System.Runtime.InteropServices;
        using Paradise.Authoring;

        namespace Whatever;

        [Guid("d1000000-0000-4000-8000-000000000001")]
        [Authored]
        public sealed record Thing
        {
            public float Value { get; set; } = 1f;
        }
        """;

    private static string RunAndGetSource(string assemblyName, string? rootNamespace)
    {
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
            MetadataReference.CreateFromFile(
                System.Reflection.Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(typeof(AuthoredAttribute).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(Source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new AuthoringSchemaGenerator().AsSourceGenerator()],
            optionsProvider: rootNamespace is null ? null : new StubOptionsProvider(rootNamespace));

        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult();
        return result.GeneratedTrees.Single().ToString();
    }

    /// <summary>The regression: `Game.Core` with RootNamespace `Game` must publish
    /// <c>Game.AuthoringSchema</c>, or no file in the project can see it unqualified.</summary>
    [Test]
    public async Task the_root_namespace_wins_over_the_assembly_name()
    {
        var generated = RunAndGetSource("Game.Core", "Game");
        await Assert.That(generated).Contains("namespace Game;");
        await Assert.That(generated).DoesNotContain("namespace Game.Core;");
    }

    /// <summary>Not every host sets RootNamespace, so the assembly name still has to work.</summary>
    [Test]
    public async Task the_assembly_name_is_the_fallback()
    {
        await Assert.That(RunAndGetSource("Game.Core", null)).Contains("namespace Game.Core;");
        await Assert.That(RunAndGetSource("Game.Core", "   ")).Contains("namespace Game.Core;");
    }

    /// <summary>An assembly name is not necessarily a legal namespace. Emitting it verbatim would
    /// produce source that does not compile, and the error would point at generated code.</summary>
    [Test]
    public async Task an_illegal_namespace_is_repaired_rather_than_emitted_verbatim()
    {
        await Assert.That(RunAndGetSource("my-game", null)).Contains("namespace my_game;");
        await Assert.That(RunAndGetSource("2fast", null)).Contains("namespace _2fast;");
    }

    /// <summary>An assembly that declares nothing emits nothing — no empty schema class to
    /// collide with a hand-written one.</summary>
    [Test]
    public async Task an_assembly_with_no_authored_types_emits_nothing()
    {
        var compilation = CSharpCompilation.Create(
            "Empty",
            [CSharpSyntaxTree.ParseText("namespace Empty; public class Nothing { }")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new AuthoringSchemaGenerator().AsSourceGenerator());
        await Assert.That(driver.RunGenerators(compilation).GetRunResult().GeneratedTrees).IsEmpty();
    }

    private sealed class StubOptionsProvider(string rootNamespace) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new StubOptions(rootNamespace);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
    }

    private sealed class StubOptions(string rootNamespace) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            if (key == "build_property.RootNamespace")
            {
                value = rootNamespace;
                return true;
            }
            value = null!;
            return false;
        }
    }
}
