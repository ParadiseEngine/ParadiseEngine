using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Paradise.Authoring.Generators;

namespace Paradise.Authoring.Test;

public class AuthoredActionDiagnosticsTests
{
    [Test]
    [Arguments("[AuthoredButton] public void Bad() {}")]
    [Arguments("[AuthoredButton] private static void Bad() {}")]
    [Arguments("[AuthoredButton] public static int Bad() => 0;")]
    [Arguments("[AuthoredButton] public static void Bad<T>() {}")]
    [Arguments("[AuthoredButton] public static async void Bad() { await System.Threading.Tasks.Task.Yield(); }")]
    [Arguments("[AuthoredButton] public static void Bad(ref AuthorActionContext context) {}")]
    [Arguments("[AuthoredButton] public static void Bad(AuthorActionContext context = null) {}")]
    [Arguments("[AuthoredButton] public static void Bad() {} public static void Bad(int n) {}")]
    [Arguments("[AuthoredToggle] public static void Bad() {}")]
    [Arguments("[AuthoredToggle] public static void Bad(AuthorActionContext context) {}")]
    [Arguments("[AuthoredToggle] public static void Bad(bool value, AuthorActionContext context) {}")]
    [Arguments("[AuthoredToggle] public static void Bad(ref bool value) {}")]
    [Arguments("[AuthoredButton, AuthoredToggle] public static void Bad(bool value) {}")]
    [Arguments("[AuthoredPreview] public static void Bad() {}")]
    [Arguments("[AuthoredPreview] public static AuthorActionOverlay? Bad() => null;")]
    [Arguments("[AuthoredPreview] public static object Bad() => new();")]
    [Arguments("[AuthoredPreview] public static AuthorActionOverlay Bad(bool value) => new() { Id = \"bad\" };")]
    [Arguments("[AuthoredPreview] public static AuthorActionOverlay Bad(ref AuthorActionContext context) => new() { Id = \"bad\" };")]
    [Arguments("[AuthoredPreview] public static AuthorActionOverlay Bad<T>() => new() { Id = \"bad\" };")]
    [Arguments("[AuthoredPreview] public static async System.Threading.Tasks.Task<AuthorActionOverlay> Bad() { await System.Threading.Tasks.Task.Yield(); return new() { Id = \"bad\" }; }")]
    [Arguments("[AuthoredPreview] public AuthorActionOverlay Bad() => new() { Id = \"bad\" };")]
    [Arguments("[AuthoredPreview] private static AuthorActionOverlay Bad() => new() { Id = \"bad\" };")]
    [Arguments("[AuthoredPreview] public static AuthorActionOverlay Bad(AuthorActionContext context = null!) => new() { Id = \"bad\" };")]
    [Arguments("[AuthoredPreview] public static AuthorActionOverlay Bad() => new() { Id = \"bad\" }; public static void Bad(int value) {}")]
    [Arguments("[AuthoredPreview, AuthoredButton] public static AuthorActionOverlay Bad() => new() { Id = \"bad\" };")]
    [Arguments("[AuthoredSave] public void Bad() {}")]
    [Arguments("[AuthoredSave] public static void Bad(bool value) {}")]
    [Arguments("[AuthoredSave] public static int Bad() => 0;")]
    [Arguments("[AuthoredSave] public static void Bad() {} public static void Bad(int n) {}")]
    [Arguments("[AuthoredSave, AuthoredPreview] public static AuthorActionOverlay Bad() => new() { Id = \"bad\" };")]
    [Arguments("[AuthoredSave, AuthoredToggle] public static void Bad() {}")]
    public async Task malformed_actions_fail_at_the_declaration(string method)
    {
        var source = """
            #nullable enable
            using Paradise.Authoring;
            using System.Runtime.InteropServices;
            [Authored, Guid("c10329ee-d564-4a3d-bdf8-2a2ccf5efa53")]
            public sealed record ActionFixture
            {
            """ + method + "\n}";
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)).ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(AuthoredAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create("ActionDiagnostics",
            [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AuthoringSchemaGenerator().AsSourceGenerator());
        var result = driver.RunGenerators(compilation).GetRunResult();

        await Assert.That(result.Diagnostics.Count(diagnostic => diagnostic.Id == "PAUT013")).IsEqualTo(1);
        await Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.Id == "CS8785")).IsFalse();
    }
}
