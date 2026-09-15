using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Paradise.ECS.Generators.Test;

public sealed class ForbiddenManagedComponentAccessAnalyzerTests
{
    private const string DiagnosticId = "PECS3015";

    [Test]
    [Arguments("IEntitySystem", "Execute")]
    [Arguments("IChunkSystem", "ExecuteChunk")]
    [Arguments("IWorldSystem", "Execute")]
    public async Task AllSystemKindsRejectReadAndWriteLookups(string systemInterface, string method)
    {
        string source = CreateSource("", systemInterface: systemInterface, method: method,
            extraMembers: "public ReadOnlyManagedLookup<Payload> Read; public ManagedLookup<Payload> Write;");

        var diagnostics = await Analyze(source);

        await Assert.That(diagnostics.Length).IsEqualTo(2);
        await Assert.That(diagnostics.All(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
        await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("Payload");
    }

    [Test]
    [Arguments("world.GetManaged<Payload>(entity);")]
    [Arguments("world.HasManaged<Payload>(entity);")]
    [Arguments("world.TryGetManaged<Payload>(entity, out _);")]
    [Arguments("world.AddManaged(entity, Application.Value);")]
    [Arguments("world.SetManaged(entity, Application.Value);")]
    [Arguments("world.RemoveManaged<Payload>(entity);")]
    [Arguments("commands.AddManaged(entity, Application.Value);")]
    [Arguments("commands.SetManaged(entity, Application.Value);")]
    [Arguments("commands.RemoveManaged<Payload>(entity);")]
    [Arguments("handle.Add<Payload>();")]
    [Arguments("handle.Get<Payload>();")]
    [Arguments("handle.Has<Payload>();")]
    [Arguments("handle.TryGet<Payload>(out _);")]
    [Arguments("handle.Set(Application.Value);")]
    [Arguments("handle.Remove<Payload>();")]
    [Arguments("var value = Application.Read[entity]!.Value;")]
    [Arguments("Application.Write[entity] = Application.Value;")]
    public async Task DirectAndInferredManagedAccessReportsOncePerStatement(string body)
    {
        var diagnostics = await Analyze(CreateSource(body));

        await Assert.That(diagnostics.Length).IsEqualTo(1);
    }

    [Test]
    [Arguments("public Payload Value;")]
    [Arguments("public Payload? Value { get; set; }")]
    [Arguments("public Payload? Read() => null;")]
    [Arguments("public void Write(Payload value) { }")]
    [Arguments("public System.Collections.Generic.List<Payload> Values = [];")]
    [Arguments("public Payload[] Values = [];")]
    [Arguments("public (Payload, int) Value;")]
    [Arguments("public Holder<Payload>.Nested Value;")]
    public async Task ManagedSignaturesAndContainersAreForbidden(string member)
    {
        var diagnostics = await Analyze(CreateSource("", extraMembers: member));

        await Assert.That(diagnostics).IsNotEmpty();
    }

    [Test]
    [Arguments("WithManaged")]
    [Arguments("WithoutManaged")]
    [Arguments("WithManagedAny")]
    public async Task ManagedSystemFiltersAreForbidden(string filter)
    {
        string source = CreateSource("").Replace("public partial class TestSystem", $$"""
            [{{filter}}<Payload>]
            public ref partial struct TestSystem
            """, StringComparison.Ordinal)
            .Replace("public static int SystemId => 1;", "public EntityCommandBuffer Commands;", StringComparison.Ordinal);

        var diagnostics = await Analyze(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
    }

    [Test]
    [Arguments("Entity")]
    [Arguments("Chunk")]
    [Arguments("Segments")]
    [Arguments("Singleton")]
    [Arguments("ReadLookup")]
    [Arguments("WriteLookup")]
    public async Task QueryViewsCarryTheirManagedPresenceClaims(string view)
    {
        string source = CreateSource("", extraMembers: $"public void Read(PayloadQuery.{view} query) {{ }}");

        var diagnostics = await Analyze(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
    }

    [Test]
    public async Task AliasesAndInheritedSystemInterfacesRemainRecognized()
    {
        string source = CreateSource("world.HasManaged<PayloadAlias>(entity);", systemInterface: "DerivedSystem") + """

            public interface DerivedSystem : IWorldSystem { }
            """;

        var diagnostics = await Analyze(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
    }

    [Test]
    public async Task GenericConstraintsAndInheritedManagedInterfacesAreRecognized()
    {
        string source = CreateSource("", extraMembers: """
            public static void Access<T>(IManagedWorld world, Entity entity) where T : class, DerivedManaged
            {
                world.HasManaged<T>(entity);
            }
            """) + """

            public interface DerivedManaged : IManagedComponent { }
            """;

        var diagnostics = await Analyze(source);

        await Assert.That(diagnostics).IsNotEmpty();
        await Assert.That(diagnostics.Any(d => d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan)
            .Contains("HasManaged", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Arguments("void Local() { world.HasManaged<Payload>(entity); } Local();")]
    [Arguments("System.Action action = () => world.HasManaged<Payload>(entity); action();")]
    [Arguments("try { world.HasManaged<Payload>(entity); } finally { }")]
    [Arguments("var value = new Payload();")]
    public async Task HelpersCallbacksAndInitializationInSystemsAreCovered(string body)
    {
        await Assert.That(await Analyze(CreateSource(body))).IsNotEmpty();
    }

    [Test]
    public async Task InheritedAndNestedHelpersRemainInSystemScope()
    {
        string source = CreateSource("", extraMembers: """
            public static class Nested
            {
                public static bool Read(IManagedWorld world, Entity entity) => world.HasManaged<Payload>(entity);
            }
            """) + """

            public sealed class Derived : TestSystem
            {
                public bool Read(IManagedWorld world, Entity entity) => world.HasManaged<Payload>(entity);
            }
            """;

        var diagnostics = await Analyze(source);

        await Assert.That(diagnostics.Length).IsEqualTo(2);
    }

    [Test]
    public async Task OptInDoesNotRestrictApplicationCodeOrOrdinaryComponents()
    {
        string source = CreateSource("""
            handle.Add<Number>();
            handle.Get<Number>();
            Application.GetManaged<int>();
            """) + """

            public sealed class Loader
            {
                public ManagedLookup<Payload> Values;
                public Payload New() => new();
                public void Load(IManagedWorld world, EntityCommandBuffer commands, WorldEntity handle, Entity entity)
                {
                    world.AddManaged(entity, new Payload());
                    commands.SetManaged(entity, new Payload());
                    handle.Get<Payload>();
                    world.GetManaged<Payload>(entity)!.Value++;
                }
            }
            """;

        await Assert.That(await Analyze(source)).IsEmpty();
    }

    [Test]
    [Arguments("_ = nameof(Payload);")]
    [Arguments("_ = nameof(PayloadAlias.Value);")]
    [Arguments("_ = nameof(Application.Value);")]
    [Arguments("_ = nameof(world.GetManaged);")]
    [Arguments("_ = typeof(Payload);")]
    [Arguments("_ = typeof(PayloadAlias[]);")]
    [Arguments("_ = typeof(ReadOnlyManagedLookup<Payload>);")]
    [Arguments("_ = typeof(PayloadQuery.Entity);")]
    [Arguments("System.Console.WriteLine($\"{nameof(Payload)}:{typeof(Payload)}\");")]
    public async Task MetadataOnlyReferencesAreAllowed(string body)
    {
        await Assert.That(await Analyze(CreateSource(body))).IsEmpty();
    }

    [Test]
    public async Task MetadataOnlyFieldsAndPropertiesAreAllowed()
    {
        string source = CreateSource("", extraMembers: """
            public static System.Type PayloadType = typeof(Payload);
            public static string ValueName => nameof(Payload.Value);
            """);

        await Assert.That(await Analyze(source)).IsEmpty();
    }

    [Test]
    [Arguments("System.Console.WriteLine(\"{0}: {1}\", typeof(Payload), world.GetManaged<Payload>(entity));", "GetManaged<Payload>")]
    [Arguments("System.Console.WriteLine(\"{0}: {1}\", nameof(Payload), Application.Value);", "Value")]
    public async Task MetadataDoesNotExemptActualAccessInTheSameStatement(string body, string expectedLocation)
    {
        var diagnostics = await Analyze(CreateSource(body));

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        var location = diagnostics[0].Location;
        var source = await location.SourceTree!.GetTextAsync();
        await Assert.That(source.ToString(location.SourceSpan)).IsEqualTo(expectedLocation);
    }

    [Test]
    public async Task OrdinaryMethodNamedNameofStillChecksItsArguments()
    {
        string source = CreateSource("_ = @nameof(Application.Value);",
            extraMembers: "private static string @nameof(object value) => value.ToString()!;");

        var diagnostics = await Analyze(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
    }

    [Test]
    public async Task AttributeIsOptInAndUsesExactSymbolIdentity()
    {
        string source = CreateSource("world.GetManaged<Payload>(entity);");
        await Assert.That(await Analyze(source.Replace("[assembly: ForbidManagedComponentsInSystems]", "", StringComparison.Ordinal))).IsEmpty();
        string unrelated = source.Replace("[assembly: ForbidManagedComponentsInSystems]",
            "[assembly: Other.ForbidManagedComponentsInSystems]", StringComparison.Ordinal) + """

            namespace Other
            {
                [System.AttributeUsage(System.AttributeTargets.Assembly)]
                public sealed class ForbidManagedComponentsInSystemsAttribute : System.Attribute { }
            }
            """;
        unrelated = unrelated.Replace("namespace ManagedRestrictionTests;", "namespace ManagedRestrictionTests {", StringComparison.Ordinal)
            .Replace("namespace Other", "} namespace Other", StringComparison.Ordinal);
        await Assert.That(await Analyze(unrelated)).IsEmpty();
    }

    [Test]
    public async Task StandardDiagnosticConfigurationStillApplies()
    {
        string source = CreateSource("world.GetManaged<Payload>(entity);");
        await Assert.That(await Analyze(source, ReportDiagnostic.Suppress)).IsEmpty();
        var diagnostics = await Analyze(source, ReportDiagnostic.Warn);
        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string source, ReportDiagnostic? severity = null)
    {
        var (result, compilation) = GeneratorTestHelper.RunGeneratorsAndCompile(source, rootNamespace: "ManagedRestrictionTests");
        await Assert.That(string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))).IsEmpty();
        await Assert.That(string.Join("\n", compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))).IsEmpty();
        if (severity is not null)
            compilation = compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions(
                compilation.Options.SpecificDiagnosticOptions.SetItem(DiagnosticId, severity.Value)));
        var diagnostics = await compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new ForbiddenManagedComponentAccessAnalyzer())).GetAnalyzerDiagnosticsAsync();
        await Assert.That(diagnostics.Any(d => d.Id == "AD0001")).IsFalse();
        return [.. diagnostics.Where(d => d.Id == DiagnosticId)];
    }

    private static string CreateSource(string body, string systemInterface = "IWorldSystem", string method = "Execute", string extraMembers = "")
        => $$"""
            using Paradise.ECS;
            using PayloadAlias = ManagedRestrictionTests.Payload;
            [assembly: ForbidManagedComponentsInSystems]
            namespace ManagedRestrictionTests;

            [ManagedComponent]
            public sealed partial class Payload { public int Value; }

            [Component]
            public partial struct Number { public int Value; }

            [Queryable(Singleton = true), WithManaged<Payload>]
            public readonly ref partial struct PayloadQuery;

            public class Holder<T> { public class Nested { } }

            public static class Application
            {
                public static Payload Value => new();
                public static ReadOnlyManagedLookup<Payload> Read => default;
                public static ManagedLookup<Payload> Write;
                public static void GetManaged<T>() { }
            }

            public partial class TestSystem : {{systemInterface}}
            {
                public static int SystemId => 1;
                public void {{method}}() { }
                public static void Access(IManagedWorld world, Entity entity, WorldEntity handle, EntityCommandBuffer commands)
                {
                    {{body}}
                }
                {{extraMembers}}
            }
            """;
}
