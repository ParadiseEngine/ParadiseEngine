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
    public async Task the_dump_matches_the_constant_byte_for_byte()
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
            await Assert.That(File.ReadAllText(output)).IsEqualTo(expected);
            await Assert.That(expected).Contains(id);
            // The type name rides along, so a dumped schema is still readable by a human.
            await Assert.That(expected).Contains("Game.Thing");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
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
