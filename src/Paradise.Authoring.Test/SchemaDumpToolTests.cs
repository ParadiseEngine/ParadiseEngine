using System.Text.Json.Nodes;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Paradise.Authoring.Generators;
using Paradise.Authoring.SchemaDump;

namespace Paradise.Authoring.Test;

/// <summary>
/// The schema-dump tool, run against an assembly it has never seen: compile a synthetic game with
/// the schema generator attached, write the dll to disk, and dump WITHOUT loading it — the tool
/// reads the constant straight from metadata, which is what lets it work on assemblies whose
/// dependencies are not restorable where it runs.
/// </summary>
public class SchemaDumpToolTests
{
    private static string CompileTo(string directory, string source, string assemblyName)
    {
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
            MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(typeof(AuthoredAttribute).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new AuthoringSchemaGenerator().AsSourceGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
        var path = Path.Combine(directory, assemblyName + ".dll");
        var emit = updated.Emit(path);
        if (!emit.Success)
        {
            throw new InvalidOperationException(string.Join(
                "\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        }
        return path;
    }

    [Test]
    public async Task the_dump_is_the_constant_indented()
    {
        var directory = Directory.CreateTempSubdirectory("schemadump").FullName;
        try
        {
            const string id = "d2000000-0000-4000-8000-000000000001";
            var dll = CompileTo(directory, $$"""
                using System.Runtime.InteropServices;
                using Paradise.Authoring;

                namespace Game;

                [Guid("{{id}}")]
                [Authored(DisplayName = "Thing")]
                public sealed record Thing
                {
                    public float Speed { get; set; } = 2.5f;
                }
                """, "Game");
            var output = Path.Combine(directory, "authoring-schema.json");

            SchemaDumper.Run(dll, output);

            // The reference value, read the heavyweight way the tool refuses to.
            var loaded = System.Reflection.Assembly.LoadFile(dll);
            var expected = (string)loaded.GetType("Game.AuthoringSchema")!
                .GetField("Json")!.GetRawConstantValue()!;
            var dumped = File.ReadAllText(output);

            // SAME DOCUMENT, not the same bytes: the file is indented for the people who read it,
            // the constant stays on one line for the assemblies that carry it. Comparing the parsed
            // forms is what makes "only the whitespace differs" an assertion rather than a claim.
            await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(dumped), JsonNode.Parse(expected))).IsTrue();

            // …and it really is indented. Without this, a regression to writing the constant
            // verbatim would still pass the equality above.
            await Assert.That(dumped).Contains("\n  ");
            await Assert.That(dumped.EndsWith('\n')).IsTrue();
            await Assert.That(expected).Contains(id);
            // The type name rides along, so a dumped schema is still readable by a human.
            await Assert.That(expected).Contains("Game.Thing");
        }
        finally
        {
            Delete(directory);
        }
    }

    [Test]
    public async Task authored_prose_keeps_its_own_characters()
    {
        // The default encoder escapes every non-ASCII character to \uXXXX, which would turn a
        // Chinese doc string into an unreadable line in the very file the indenting exists to make
        // readable. Games author prose in their own language; the dump has to survive it.
        var directory = Directory.CreateTempSubdirectory("schemadump").FullName;
        try
        {
            var dll = CompileTo(directory, """
                using System.Runtime.InteropServices;
                using Paradise.Authoring;

                namespace Game;

                [Guid("d2000000-0000-4000-8000-000000000002")]
                [Authored(DisplayName = "价值")]
                public sealed record Thing
                {
                    [AuthorDoc("普通硬币。")]
                    public float Speed { get; set; } = 2.5f;
                }
                """, "Game");
            var output = Path.Combine(directory, "authoring-schema.json");

            SchemaDumper.Run(dll, output);

            var dumped = File.ReadAllText(output);
            await Assert.That(dumped).Contains("价值");
            await Assert.That(dumped).Contains("普通硬币。");
            await Assert.That(dumped).DoesNotContain("\\u");
        }
        finally
        {
            Delete(directory);
        }
    }

    /// <summary>
    /// Best-effort cleanup of a temp directory holding an assembly this test LOADED.
    /// </summary>
    /// <remarks>
    /// Windows locks a file for as long as it is loaded and <see cref="System.Reflection.Assembly.LoadFile"/>
    /// gives no way to unload it, so a plain recursive delete throws
    /// <see cref="UnauthorizedAccessException"/> here and fails an otherwise passing test — on
    /// Windows only, which is why CI never saw it. The directory is under the OS temp root, so
    /// leaving it costs nothing.
    /// </remarks>
    private static void Delete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
        }
    }

    [Test]
    public async Task an_assembly_without_a_schema_names_the_problem()
    {
        var directory = Directory.CreateTempSubdirectory("schemadump").FullName;
        try
        {
            var dll = CompileTo(directory, """
                namespace Game;
                public sealed class Nothing;
                """, "Bare");

            var thrown = Assert.Throws<InvalidOperationException>(
                () => SchemaDumper.Run(dll, Path.Combine(directory, "out.json")));
            await Assert.That(thrown!.Message).Contains("AuthoringSchema");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
