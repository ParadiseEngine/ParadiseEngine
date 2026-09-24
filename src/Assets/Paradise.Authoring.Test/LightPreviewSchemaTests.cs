using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Paradise.Authoring.Generators;

namespace Paradise.Authoring.Test;

public class LightPreviewSchemaTests
{
    internal const string Source = """
        using System.Runtime.InteropServices;
        using Paradise.Authoring;

        namespace Preview;

        [Guid("b4100000-0000-4000-8000-000000000001"), Authored]
        [AuthorLightPreview(HostLightType.Spot)]
        public sealed record Lamp
        {
            [AuthorLightField(LightPreviewField.Intensity)]
            public float Power { get; set; } = 2.5f;

            public Cone Shape { get; set; } = new();
            public string Label { get; set; } = "lamp";
        }

        public sealed record Cone
        {
            [AuthorLightField(LightPreviewField.OuterDegrees)]
            public float Width { get; set; } = 60f;
        }

        [Guid("b4100000-0000-4000-8000-000000000002"), Authored]
        [AuthorLightPreview(HostLightType.Point)]
        public struct Bulb;

        [Guid("b4100000-0000-4000-8000-000000000003"), Authored]
        [AuthorLightPreview(HostLightType.Directional)]
        public struct Sun;

        [Guid("b4100000-0000-4000-8000-000000000004"), Authored]
        public struct Other;
        """;

    private static AuthoringSchemaDocument Generate()
    {
        var compilation = CSharpCompilation.Create(
            "Preview",
            [CSharpSyntaxTree.ParseText(Source)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
                MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
                MetadataReference.CreateFromFile(typeof(AuthoredAttribute).Assembly.Location),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new AuthoringSchemaGenerator().AsSourceGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var diagnostics);
        var errors = diagnostics.Concat(generated.GetDiagnostics())
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0)
        {
            throw new InvalidOperationException(string.Join("; ", errors.Select(d => d.ToString())));
        }
        var constant = generated.GetTypeByMetadataName("Preview.AuthoringSchema")!
            .GetMembers("Json").OfType<IFieldSymbol>().Single();
        return AuthoringSchemaReader.Read((string)constant.ConstantValue!);
    }

    [Test]
    public async Task preview_semantics_are_emitted_without_game_type_or_field_name_conventions()
    {
        var schema = Generate();
        var lamp = schema.Components.Single(c => c.Type == "Preview.Lamp");
        await Assert.That(lamp.PreviewLight).IsEqualTo("Spot");
        await Assert.That(lamp.Fields.Single(f => f.Name == "Power").LightField).IsEqualTo("Intensity");
        await Assert.That(lamp.Fields.Single(f => f.Name == "Power").Default!.Value.GetSingle()).IsEqualTo(2.5f);
        await Assert.That(lamp.Fields.Single(f => f.Name == "Shape").Fields!.Single().LightField)
            .IsEqualTo("OuterDegrees");
        await Assert.That(lamp.Fields.Single(f => f.Name == "Label").LightField).IsNull();
        await Assert.That(schema.Components.Single(c => c.Type == "Preview.Bulb").PreviewLight).IsEqualTo("Point");
        await Assert.That(schema.Components.Single(c => c.Type == "Preview.Sun").PreviewLight).IsEqualTo("Directional");
        await Assert.That(schema.Components.Single(c => c.Type == "Preview.Other").PreviewLight).IsNull();
    }

    [Test]
    public async Task typed_schema_roundtrip_preserves_optional_preview_metadata()
    {
        var original = AuthoringSchemaReader.Write(Generate());
        var parsed = AuthoringSchemaReader.Read(original);
        var rewritten = AuthoringSchemaReader.Write(parsed);
        await Assert.That(rewritten).IsEqualTo(original);
        var lamp = parsed.Components.Single(c => c.Type == "Preview.Lamp");
        await Assert.That(lamp.PreviewLight).IsEqualTo("Spot");
        await Assert.That(lamp.Fields.Single(f => f.Name == "Power").LightField).IsEqualTo("Intensity");
    }
}
