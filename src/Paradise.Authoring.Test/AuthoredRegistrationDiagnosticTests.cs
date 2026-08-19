using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Paradise.Authoring.Generators;

namespace Paradise.Authoring.Test;

/// <summary>
/// PAUT001: an [Authored] type the JsonSerializerContext does not serialize.
///
/// The registry deserializes through <c>Ctx.Default.Foo</c>, a property System.Text.Json's
/// generator only emits for types registered with [JsonSerializable]. Left unchecked, forgetting
/// that attribute failed the build with CS1061 INSIDE the generated registry — a generated file
/// naming a generated member, which reads like a toolchain bug rather than a missing line in the
/// game's own source. The generator cannot add the registration for you: System.Text.Json's
/// generator only ever sees the original compilation.
/// </summary>
public class AuthoredRegistrationDiagnosticTests
{
    private static string Source(bool registered) => $$"""
        using System.Text.Json.Serialization;
        using Paradise.Authoring;

        [assembly: AuthoredJsonContext(typeof(Game.Ctx))]

        namespace Game;

        [Authored("game.thing")]
        public sealed record Thing
        {
            public float Value { get; set; } = 1f;
        }

        {{(registered ? "[JsonSerializable(typeof(Thing))]" : "")}}
        public partial class Ctx : JsonSerializerContext;
        """;

    private static GeneratorDriverRunResult Run(bool registered)
    {
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
            MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(typeof(AuthoredAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Text.Json.JsonSerializer).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            "Game",
            [CSharpSyntaxTree.ParseText(Source(registered))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new AuthoredRegistryGenerator().AsSourceGenerator());
        return driver.RunGenerators(compilation).GetRunResult();
    }

    /// <summary>The whole point: the error names the record and the fix, at the record.</summary>
    [Test]
    public async Task an_unregistered_authored_type_is_reported_against_its_own_declaration()
    {
        var diagnostic = Run(registered: false).Diagnostics.Single();

        await Assert.That(diagnostic.Id).IsEqualTo("PAUT001");
        await Assert.That(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .Contains("Add [JsonSerializable(typeof(Thing))]");
        // Pointing at the record itself, not at the generated file that used to fail with CS1061.
        // (A synthetic tree has no path, so the span's TEXT is what proves where it landed.)
        await Assert.That(diagnostic.Location.Kind).IsEqualTo(LocationKind.SourceFile);
        var text = diagnostic.Location.SourceTree!.GetText()
            .GetSubText(diagnostic.Location.SourceSpan).ToString();
        await Assert.That(text).IsEqualTo("Thing");
    }

    /// <summary>Reported AND skipped: emitting the case anyway would bury PAUT001 under the
    /// CS1061 it exists to replace.</summary>
    [Test]
    public async Task an_unregistered_type_is_left_out_of_the_registry()
    {
        var generated = Run(registered: false).GeneratedTrees;

        await Assert.That(generated.Length == 0 || !generated.Single().ToString().Contains("Ctx.Default.Thing"))
            .IsTrue();
    }

    /// <summary>And a registered type is silent, and still reachable.</summary>
    [Test]
    public async Task a_registered_authored_type_generates_without_complaint()
    {
        var result = Run(registered: true);

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.GeneratedTrees.Single().ToString()).Contains("Default.Thing");
    }
}
