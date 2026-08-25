using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Paradise.Authoring.Generators;

namespace Paradise.Authoring.Test;

/// <summary>
/// <c>ParadiseAuthoringScanReferences</c>: one assembly publishing the schema for everything it
/// references.
///
/// The case it exists for is a game split across projects — components in Core, systems in Game,
/// hosting in Launcher — where the assembly that sees the WHOLE game is the launcher, and the
/// launcher declares no components at all. Without this it would dump an empty document, and the
/// schema file would have to come from whichever library happened to hold the records.
///
/// Every test here drives the generator over a REAL referenced assembly rather than a stub,
/// because the thing most likely to break is what survives the round trip through metadata: the
/// merge reads each reference's generated constant precisely so that field defaults — property
/// initializers, which exist only in syntax and are gone from a compiled symbol — come through.
/// A stub reference would prove nothing about that.
/// </summary>
public class AuthoringSchemaReferenceScanTests
{
    private const string LibraryId = "a1000000-0000-4000-8000-000000000001";
    private const string LauncherId = "a1000000-0000-4000-8000-000000000002";

    /// <summary>A component with a DEFAULT on it. The default is the point: it is the part that
    /// cannot survive being re-read from metadata.</summary>
    private const string LibrarySource = $$"""
        using System.Runtime.InteropServices;
        using Paradise.Authoring;

        namespace Library;

        [Guid("{{LibraryId}}")]
        [Authored(DisplayName = "From The Library")]
        public sealed record LibraryComponent
        {
            public float Speed { get; set; } = 2.5f;
        }
        """;

    private static ImmutableArray<MetadataReference> BaseReferences() =>
    [
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
        MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
        MetadataReference.CreateFromFile(typeof(AuthoredAttribute).Assembly.Location),
    ];

    /// <summary>Compile <paramref name="source"/> all the way to an assembly — generators run, and
    /// the emitted image is what a downstream compilation will actually see.</summary>
    private static MetadataReference Build(string name, string source)
    {
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            BaseReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new AuthoringSchemaGenerator().AsSourceGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        var image = new MemoryStream();
        var emitted = updated.Emit(image);
        if (!emitted.Success)
        {
            throw new InvalidOperationException(
                $"'{name}' did not compile: "
                + string.Join("; ", emitted.Diagnostics.Where(d =>
                    d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())));
        }
        image.Position = 0;
        return MetadataReference.CreateFromStream(image);
    }

    /// <summary>Run the generator over a consumer of <paramref name="references"/>, returning the
    /// document it published (null when it published none) and what it reported.</summary>
    private static (string? Json, ImmutableArray<Diagnostic> Diagnostics) Consume(
        string source, bool scan, params MetadataReference[] references)
    {
        var compilation = CSharpCompilation.Create(
            "Consumer",
            [CSharpSyntaxTree.ParseText(source)],
            BaseReferences().AddRange(references),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new AuthoringSchemaGenerator().AsSourceGenerator()],
            optionsProvider: new ScanOptionsProvider(scan));
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult();
        var tree = result.GeneratedTrees.SingleOrDefault();
        return (tree is null ? null : Constant(tree.ToString()), result.Diagnostics);
    }

    /// <summary>The <c>Json</c> constant out of the generated source, back to plain JSON: the
    /// generator writes it as a verbatim literal with its quotes doubled.</summary>
    private static string Constant(string generated)
    {
        const string opening = "public const string Json = @\"";
        var start = generated.IndexOf(opening, StringComparison.Ordinal) + opening.Length;
        var end = generated.LastIndexOf("\";", StringComparison.Ordinal);
        return generated[start..end].Replace("\"\"", "\"");
    }

    private const string DeclaresNothing = "namespace Consumer; public static class Nothing { }";

    /// <summary>The headline: a project that declares no components of its own publishes the
    /// schema of everything it references.</summary>
    [Test]
    public async Task a_launcher_publishes_the_components_of_what_it_references()
    {
        var (json, _) = Consume(DeclaresNothing, scan: true, Build("Library", LibrarySource));

        await Assert.That(json).IsNotNull();
        var schema = AuthoringSchemaReader.Read(json!);
        await Assert.That(schema.Version).IsEqualTo(AuthoringSchemaDocument.CurrentVersion);
        await Assert.That(schema.Components.Select(c => c.Type))
            .IsEquivalentTo(new[] { "Library.LibraryComponent" });
        await Assert.That(schema.Components.Single().DisplayName).IsEqualTo("From The Library");
    }

    /// <summary>The reason the merge takes each reference's generated CONSTANT rather than
    /// re-reading its types: a default is a property initializer, so it lives in syntax and is
    /// absent from every symbol loaded from metadata. Re-deriving would publish this component
    /// with the field present and its default silently gone, and an editor would offer 0 where the
    /// game means 2.5.</summary>
    [Test]
    public async Task a_merged_component_keeps_the_field_defaults_metadata_does_not_carry()
    {
        var (json, _) = Consume(DeclaresNothing, scan: true, Build("Library", LibrarySource));

        var field = AuthoringSchemaReader.Read(json!).Components.Single().Fields.Single();
        await Assert.That(field.Name).IsEqualTo("Speed");
        await Assert.That(field.Default.HasValue).IsTrue();
        await Assert.That(field.Default!.Value.GetSingle()).IsEqualTo(2.5f);
    }

    /// <summary>Off unless asked for. Every project that references Paradise.Export references an
    /// assembly publishing a schema, so scanning by default would silently widen the document
    /// every existing game dumps.</summary>
    [Test]
    public async Task scanning_is_off_by_default()
    {
        var (json, _) = Consume(DeclaresNothing, scan: false, Build("Library", LibrarySource));
        await Assert.That(json).IsNull();
    }

    /// <summary>Local declarations and referenced ones in ONE document, ordered by type name like
    /// any other — a consumer cannot tell which half a component came from, which is what makes
    /// moving a component between projects invisible to the editors.</summary>
    [Test]
    public async Task local_and_referenced_components_merge_into_one_ordered_document()
    {
        var (json, _) = Consume(
            $$"""
            using System.Runtime.InteropServices;
            using Paradise.Authoring;

            namespace Consumer;

            [Guid("{{LauncherId}}")]
            [Authored]
            public sealed record ConsumerComponent
            {
                public int Count { get; set; } = 3;
            }
            """,
            scan: true,
            Build("Library", LibrarySource));

        await Assert.That(AuthoringSchemaReader.Read(json!).Components.Select(c => c.Type))
            .IsEquivalentTo(new[] { "Consumer.ConsumerComponent", "Library.LibraryComponent" });
    }

    /// <summary>Earlier wins, and the local declaration is earliest — the same rule
    /// <c>AuthoringSchemaReader.Merge</c> applies at load. Warned rather than errored because
    /// neither assembly is necessarily this project's to fix.</summary>
    [Test]
    public async Task a_local_component_wins_an_id_a_reference_already_claims()
    {
        var (json, diagnostics) = Consume(
            $$"""
            using System.Runtime.InteropServices;
            using Paradise.Authoring;

            namespace Consumer;

            [Guid("{{LibraryId}}")]
            [Authored(DisplayName = "Mine")]
            public sealed record Clashing
            {
                public int Count { get; set; }
            }
            """,
            scan: true,
            Build("Library", LibrarySource));

        var components = AuthoringSchemaReader.Read(json!).Components;
        await Assert.That(components.Count).IsEqualTo(1);
        await Assert.That(components.Single().Type).IsEqualTo("Consumer.Clashing");
        await Assert.That(diagnostics.Select(d => d.Id)).Contains("PAUT008");
    }

    /// <summary>A reference built against another engine describes its fields by another set of
    /// rules, so it is skipped and SAID so. Half-merging it would publish components this build
    /// would render wrongly.</summary>
    [Test]
    public async Task a_reference_at_another_schema_version_is_skipped_and_reported()
    {
        var stale = Build("Stale", """
            namespace Stale;

            public static class AuthoringSchema
            {
                public const string Json =
                    @"{""version"":2,""components"":[{""id"":""a1000000-0000-4000-8000-00000000000f"",""type"":""Stale.Old"",""displayName"":""Old"",""fields"":[]}]}";
            }

            internal static class Anchor
            {
                // Records the Paradise.Authoring assembly reference the scan filters on, without
                // declaring an [Authored] type — which would make the generator emit a SECOND
                // AuthoringSchema into this assembly and fail the compile.
                public static readonly System.Type Marker = typeof(Paradise.Authoring.AuthoredAttribute);
            }
            """);

        var (json, diagnostics) = Consume(DeclaresNothing, scan: true, stale);

        await Assert.That(json).IsNull();
        var reported = diagnostics.Single(d => d.Id == "PAUT007");
        await Assert.That(reported.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("Stale");
        await Assert.That(reported.Severity).IsEqualTo(DiagnosticSeverity.Warning);
    }

    private sealed class ScanOptionsProvider(bool scan) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new ScanOptions(scan);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
    }

    private sealed class ScanOptions(bool scan) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            if (key == "build_property.ParadiseAuthoringScanReferences")
            {
                value = scan ? "true" : "false";
                return true;
            }
            value = null!;
            return false;
        }
    }
}
