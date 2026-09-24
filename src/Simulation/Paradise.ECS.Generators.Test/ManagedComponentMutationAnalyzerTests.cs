using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Paradise.ECS.Generators.Test;

public sealed class ManagedComponentMutationAnalyzerTests
{
    private const string DiagnosticId = "PECS3014";

    [Test]
    [Arguments("Read[entity]!.Health = 1;")]
    [Arguments("Read[entity]!.Health += 1;")]
    [Arguments("Read[entity]!.Health++;")]
    [Arguments("--Read[entity]!.Health;")]
    [Arguments("Read[entity]!.Count ??= 1;")]
    [Arguments("Read[entity]!.Child.Value = 1;")]
    [Arguments("Read[entity]!.Position.X++;")]
    [Arguments("Read[entity]!.Samples[0] = 1;")]
    [Arguments("Read[entity]!.Values[0] = 1;")]
    [Arguments("Increment(ref Read[entity]!.Health);")]
    [Arguments("Overwrite(out Read[entity]!.Health);")]
    [Arguments("Write[entity]!.Health++;")]
    [Arguments("Read[entity]!.Changed += Handler;")]
    public async Task ExistingObjectWrites_ReportAWarning(string body)
    {
        var diagnostics = await Analyze(body);
        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("Payload");
    }

    [Test]
    [Arguments("var value = Read[entity]!; var alias = value; alias.Health++;")]
    [Arguments("if (Read.TryGet(entity, out var value)) value!.Health++;")]
    [Arguments("var child = Read[entity]!.Child; child.Value++;")]
    [Arguments("var samples = Read[entity]!.Samples; samples[0]++;")]
    [Arguments("var values = Read[entity]!.Values; values.Add(1);")]
    [Arguments("ICollection<int> values = Read[entity]!.Values; values.Clear();")]
    [Arguments("ref var position = ref Read[entity]!.Position; position.X++;")]
    [Arguments("var position = Read[entity]!.Position; position.Values.Add(1);")]
    [Arguments("var value = new Payload(); if (Read.Has(entity)) value = Read[entity]!; value.Health++;")]
    [Arguments("var value = new Payload(); while (Read.Has(entity)) { value = Read[entity]!; break; } value.Health++;")]
    [Arguments("var value = Read.Has(entity) ? Read[entity]! : new Payload(); value.Health++;")]
    public async Task AliasesAndControlFlow_PreservePossibleManagedOrigins(string body)
    {
        var diagnostics = await Analyze(body);
        await Assert.That(diagnostics.Length).IsEqualTo(1);
    }

    [Test]
    [Arguments("ref var health = ref Read[entity]!.Health; health = 1; health = 2;", 2)]
    [Arguments("ref var health = ref Read[entity]!.Health; Overwrite(out health); health = 2;", 2)]
    [Arguments("var position = Read[entity]!.Position; ref var x = ref position.X; x++;", 0)]
    [Arguments("ref var health = ref Read[entity]!.Health; var fresh = new Payload(); health = ref fresh.Health; health++;", 0)]
    public async Task RefAliasesTrackStorageIndependentlyFromTheirValues(string body, int expected)
    {
        await Assert.That((await Analyze(body)).Length).IsEqualTo(expected);
    }

    [Test]
    [Arguments("", "((1, 2), 3)")]
    [Arguments("var values = ((1, 2), 3);", "values")]
    [Arguments("static ((int, int), int) Values() => ((1, 2), 3);", "Values()")]
    public async Task NestedDeconstruction_ReportsEveryBorrowedTarget(string setup, string value)
    {
        var diagnostics = await Analyze($$"""
            {{setup}}
            ((Read[entity]!.Health, Read[entity]!.Child.Value), Read[entity]!.Samples[0]) = {{value}};
            """);
        await Assert.That(diagnostics.Length).IsEqualTo(3);
    }

    [Test]
    [Arguments("((first, second), third) = ((second, third), first);")]
    [Arguments("(first, (second, third)) = (second, (third, first));")]
    public async Task NestedDeconstruction_PreservesParallelAliasAssignments(string assignment)
    {
        var diagnostics = await Analyze($$"""
            var first = Read[entity]!.Child;
            var second = new Child();
            var third = new Child();
            {{assignment}}
            first.Value++;
            second.Value++;
            third.Value++;
            """);
        await Assert.That(diagnostics.Length).IsEqualTo(1);
        var location = diagnostics[0].Location;
        var source = await location.SourceTree!.GetTextAsync();
        await Assert.That(source.ToString(location.SourceSpan)).IsEqualTo("third.Value");
    }

    [Test]
    [Arguments("Read[entity]!.Values.Add(1);")]
    [Arguments("Read[entity]!.Values.Clear();")]
    [Arguments("Read[entity]!.Values.Remove(1);")]
    [Arguments("Read[entity]!.Values.Sort();")]
    [Arguments("Read[entity]!.Map.Add(1, 2);")]
    [Arguments("Read[entity]!.Map.Remove(1);")]
    [Arguments("System.Array.Clear(Read[entity]!.Samples);")]
    [Arguments("System.Array.Fill(Read[entity]!.Samples, 1);")]
    [Arguments("System.Collections.IList values = Read[entity]!.Values; values.Insert(0, 1);")]
    [Arguments("System.Collections.IList values = Read[entity]!.Values; values.RemoveAt(0);")]
    [Arguments("Read[entity]!.Samples.SetValue(1, 0);")]
    public async Task KnownCollectionMutations_ReportAWarning(string body)
    {
        var diagnostics = await Analyze(body);
        await Assert.That(diagnostics.Length).IsEqualTo(1);
    }

    [Test]
    [Arguments("var value = Read[entity]!.Health;")]
    [Arguments("var count = Read[entity]!.Values.Count;")]
    [Arguments("var found = Read[entity]!.Values.Contains(1);")]
    [Arguments("var text = Read[entity]!.ToString();")]
    [Arguments("var read = Read[entity]!.Child.Add(1);")]
    [Arguments("var value = new Payload { Health = Read[entity]!.Health + 1 };")]
    [Arguments("var value = new Payload(); value.Health++; value.Values.Add(1);")]
    [Arguments("var value = Read[entity]!; value = new Payload(); value.Health++;")]
    [Arguments("var value = Read[entity]!; if (Read.Has(entity)) value = new Payload(); else value = new Payload(); value.Health++;")]
    [Arguments("var position = Read[entity]!.Position; position.X++;")]
    [Arguments("Write[entity] = new Payload { Health = Read[entity]!.Health + 1 };")]
    [Arguments("Write.Set(entity, new Payload { Health = Read[entity]!.Health + 1 });")]
    [Arguments("Write.TrySet(entity, new Payload { Health = 1 });")]
    [Arguments("Commands.SetManaged(entity, new Payload { Health = 1 });")]
    [Arguments("Commands.AddManaged(entity, new Payload { Health = 1 });")]
    public async Task ReadsInitializationAndTrackedReplacement_DoNotWarn(string body)
    {
        await Assert.That(await Analyze(body)).IsEmpty();
    }

    [Test]
    [Arguments("IEntitySystem", "Execute")]
    [Arguments("IChunkSystem", "ExecuteChunk")]
    [Arguments("IWorldSystem", "Execute")]
    public async Task AllSystemKinds_AreCovered(string systemInterface, string method)
    {
        var diagnostics = await Analyze("Read[entity]!.Health++;", systemInterface, method);
        await Assert.That(diagnostics.Length).IsEqualTo(1);
    }

    [Test]
    public async Task CloneSnapshotPolicy_DoesNotExemptMutation()
    {
        var source = CreateSource("Read[entity]!.Health++;")
            .Replace("[ManagedComponent]", "[ManagedComponent(Snapshot = ManagedSnapshot.Clone)]", StringComparison.Ordinal)
            .Replace("public sealed partial class Payload", "public sealed partial class Payload : IManagedClone<Payload>", StringComparison.Ordinal)
            .Replace("public int Health;", "public static Payload Clone(Payload source) => new() { Health = source.Health }; public int Health;", StringComparison.Ordinal);
        await Assert.That((await AnalyzeSource(source)).Length).IsEqualTo(1);
    }

    [Test]
    [Arguments("world.GetManaged<Payload>(entity)!.Health++;")]
    [Arguments("if (world.TryGetManaged<Payload>(entity, out var value)) value!.Health++;")]
    [Arguments("handle.Get<Payload>()!.Health++;")]
    [Arguments("if (handle.TryGet<Payload>(out var value)) value!.Health++;")]
    [Arguments("payload.Health++;")]
    public async Task SystemHelpers_RecognizeWorldAccessAndManagedParameters(string body)
    {
        var source = CreateSource("", extraMembers: $$"""
            public static void Update(IManagedWorld world, WorldEntity handle, Entity entity, Payload payload)
            {
                {{body}}
            }
            """);
        await Assert.That((await AnalyzeSource(source)).Length).IsEqualTo(1);
    }

    [Test]
    public async Task OrdinaryApplicationCode_IsNotDiagnosed()
    {
        var source = CreateSource("") + """

            public static class Application
            {
                public static void Update(IManagedWorld world, Entity entity)
                {
                    world.GetManaged<Payload>(entity)!.Health++;
                }
            }
            """;
        await Assert.That(await AnalyzeSource(source)).IsEmpty();
    }

    [Test]
    [Arguments("try { Throw(); } catch (System.Exception) { Read[entity]!.Health++; }")]
    [Arguments("try { Throw(); } finally { Read[entity]!.Health++; }")]
    public async Task DirectAccessInExceptionHandlers_IsDiagnosed(string body)
    {
        var source = CreateSource(body, extraMembers: "private static void Throw() => throw new System.InvalidOperationException();");
        await Assert.That((await AnalyzeSource(source)).Length).IsEqualTo(1);
    }

    [Test]
    public async Task HandWrittenManagedComponent_IsRecognized()
    {
        var source = CreateSource("", extraMembers: "public static void Update(Manual value) { value.Health++; }") + """

            public sealed class Manual : IManagedComponent
            {
                public static ComponentId SlotTypeId => new(12);
                public static System.Guid Guid => System.Guid.Empty;
                public int Health;
            }
            """;
        await Assert.That((await AnalyzeSource(source)).Length).IsEqualTo(1);
    }

    [Test]
    public async Task WarningCanBeSuppressedOrPromoted()
    {
        var suppressed = await Analyze("""
            #pragma warning disable PECS3014
            Read[entity]!.Health++;
            #pragma warning restore PECS3014
            """);
        await Assert.That(suppressed).IsEmpty();
        var promoted = await AnalyzeSource(CreateSource("Read[entity]!.Health++;"), ReportDiagnostic.Error);
        await Assert.That(promoted.Length).IsEqualTo(1);
        await Assert.That(promoted[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    private static Task<ImmutableArray<Diagnostic>> Analyze(string body,
        string systemInterface = "IWorldSystem", string method = "Execute")
        => AnalyzeSource(CreateSource(body, systemInterface, method));

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeSource(string source, ReportDiagnostic? severity = null)
    {
        var (result, compilation) = GeneratorTestHelper.RunGeneratorsAndCompile(source, rootNamespace: "ManagedMutationTests");
        await Assert.That(string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))).IsEmpty();
        await Assert.That(string.Join("\n", compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))).IsEmpty();
        if (severity is not null)
            compilation = compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions(
                compilation.Options.SpecificDiagnosticOptions.SetItem(DiagnosticId, severity.Value)));
        var diagnostics = await compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new ManagedComponentMutationAnalyzer())).GetAnalyzerDiagnosticsAsync();
        await Assert.That(diagnostics.Any(d => d.Id == "AD0001")).IsFalse();
        return [.. diagnostics.Where(d => d.Id == DiagnosticId)];
    }

    private static string CreateSource(string body, string systemInterface = "IWorldSystem", string method = "Execute", string extraMembers = "")
        => $$"""
            using System.Collections.Generic;
            using Paradise.ECS;
            namespace ManagedMutationTests;

            [ManagedComponent]
            public sealed partial class Payload
            {
                public int Health;
                public int? Count { get; set; }
                public Child Child = new();
                public Position Position;
                public int[] Samples = new int[2];
                public List<int> Values = new();
                public Dictionary<int, int> Map = new();
                public event System.Action? Changed;
            }

            public struct Position
            {
                public int X;
                public List<int> Values;
            }

            public sealed class Child
            {
                public int Value;
                public int Add(int value) => Value + value;
            }

            public ref partial struct TestSystem : {{systemInterface}}
            {
                public ReadOnlyManagedLookup<Payload> Read;
                {{(systemInterface == "IWorldSystem" ? "public ManagedLookup<Payload> Write;" : "")}}
                public EntityCommandBuffer Commands;

                public void {{method}}()
                {
                    Entity entity = default;
                    {{body}}
                }

                private static void Increment(ref int value) => value++;
                private static void Overwrite(out int value) => value = 1;
                private static void Handler() { }
                {{extraMembers}}
            }
            """;
}
