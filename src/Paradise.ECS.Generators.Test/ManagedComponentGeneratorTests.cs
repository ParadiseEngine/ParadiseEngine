using Microsoft.CodeAnalysis;

namespace Paradise.ECS.Generators.Test;

public class ManagedComponentGeneratorTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task AliasMatrix_CompilesFactoriesAndQueries(bool tags, bool managed)
    {
        var source = $$"""
            using Paradise.ECS;
            namespace TestNamespace;
            [Component] public partial struct Position { public int Value; }
            {{(tags ? "[Tag] public partial struct Active;" : "")}}
            {{(managed ? "[ManagedComponent] public sealed partial class Payload { public string Value = string.Empty; }" : "")}}
            public static class Consumer
            {
                public static void Use()
                {
                    using SharedWorld shared = SharedWorldFactory.Create();
                    using SharedWorld configured = SharedWorldFactory.Create(new DefaultConfig());
                    World world = shared.CreateWorld();
                    Entity entity = world.Spawn();
                    {{(managed ? "world.AddManaged(entity, new Payload());" : "")}}
                    {{(tags ? "world.AddTag<Active>(entity); world.RemoveTag<Active>(entity); _ = world.HasTag<Active>(entity); _ = world.GetTags(entity);" : "")}}
                    EntityQueryResult entities = QueryBuilder.Create().Build(world);
                    EntityChunkQueryResult chunks = QueryBuilder.Create().BuildChunk(world);
                    _ = entities;
                    _ = chunks;
                }
            }
            """;
        var (result, compilation) = GeneratorTestHelper.RunGeneratorsAndCompile(source, rootNamespace: "TestNamespace");
        await Assert.That(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(string.Join("\n", compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))).IsEmpty();
        var aliases = GeneratedSource(result, "ComponentAliases.g.cs");
        await Assert.That(aliases.Contains("global using World = global::Paradise.ECS.ManagedWorld", StringComparison.Ordinal)).IsEqualTo(managed);
        await Assert.That(aliases.Contains("TaggedWorld<", StringComparison.Ordinal)).IsEqualTo(tags);
        await Assert.That(result.GeneratedTrees.Any(t => Path.GetFileName(t.FilePath) == "ManagedRegistry.g.cs")).IsEqualTo(managed);
    }

    [Test]
    public async Task ManagedReferenceWithoutValidClass_PreservesPlainAliases()
    {
        const string source = """
            using Paradise.ECS;
            [Component] public partial struct Position { public int Value; }
            [ManagedComponent] public partial class NotSealed;
            """;
        var result = GeneratorTestHelper.RunGenerator(source, includeManagedReference: true);
        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS032")).IsTrue();
        await Assert.That(GeneratedSource(result, "ComponentAliases.g.cs")).DoesNotContain("ManagedWorld");
        await Assert.That(result.GeneratedTrees.Any(t => Path.GetFileName(t.FilePath) == "ManagedRegistry.g.cs")).IsFalse();
    }

    [Test]
    public async Task AttributeWithoutManagedReference_DoesNotEnableManagedAliases()
    {
        const string source = """
            using Paradise.ECS;
            namespace Paradise.ECS
            {
                [System.AttributeUsage(System.AttributeTargets.Class)]
                public sealed class ManagedComponentAttribute : System.Attribute;
            }
            namespace TestNamespace
            {
                [Component] public partial struct Position { public int Value; }
                [ManagedComponent] public sealed partial class Payload;
            }
            """;
        var result = GeneratorTestHelper.RunGenerator(source, includeManagedReference: false);
        await Assert.That(GeneratedSource(result, "ComponentAliases.g.cs")).DoesNotContain("ManagedWorld");
        await Assert.That(result.GeneratedTrees.Any(t => Path.GetFileName(t.FilePath) == "ManagedRegistry.g.cs")).IsFalse();
    }

    [Test]
    [Arguments("[ManagedComponent] public sealed class Payload;", "PECS030")]
    [Arguments("[ManagedComponent] public partial struct Payload;", "PECS030")]
    [Arguments("[ManagedComponent] public partial class Payload;", "PECS032")]
    [Arguments("[ManagedComponent(Snapshot = ManagedSnapshot.Clone)] public sealed partial class Payload;", "PECS031")]
    [Arguments("[ManagedComponent(Snapshot = (ManagedSnapshot)77)] public sealed partial class Payload;", "PECS038")]
    [Arguments("[ManagedComponent(\"invalid\")] public sealed partial class Payload;", "PECS036")]
    [Arguments("[ManagedComponent] public sealed partial class Payload<T>;", "PECS005")]
    [Arguments("public partial class Outer<T> { [ManagedComponent] public sealed partial class Payload; }", "PECS005")]
    [Arguments("public partial class Outer { [ManagedComponent] private sealed partial class Payload; }", "PECS039")]
    [Arguments("public class Outer { [ManagedComponent] public sealed partial class Payload; }", "PECS030")]
    [Arguments("[Component] public partial struct Managed_Payload { public int Handle; }", "PECS033")]
    public async Task InvalidManagedDeclarations_ReportDiagnostic(string declaration, string diagnostic)
    {
        var result = GeneratorTestHelper.RunGenerator("using Paradise.ECS; " + declaration, includeManagedReference: true);
        await Assert.That(result.Diagnostics.Any(d => d.Id == diagnostic)).IsTrue();
        await Assert.That(result.GeneratedTrees.Any(t => Path.GetFileName(t.FilePath) == "ManagedRegistry.g.cs")).IsFalse();
    }

    [Test]
    public async Task CloneWithExplicitInterfaceImplementation_Compiles()
    {
        const string source = """
            using Paradise.ECS;
            namespace TestNamespace;
            [ManagedComponent(Snapshot = ManagedSnapshot.Clone)]
            public sealed partial class Payload : IManagedClone<Payload>
            {
                static Payload IManagedClone<Payload>.Clone(Payload source) => new();
            }
            """;
        var (result, compilation) = GeneratorTestHelper.RunGeneratorsAndCompile(source, rootNamespace: "TestNamespace");
        await Assert.That(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(string.Join("\n", compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))).IsEmpty();
        await Assert.That(GeneratedSource(result, "ManagedRegistry.g.cs")).Contains("ManagedTypeInfo.CreateClone<global::TestNamespace.Payload>()");
    }

    [Test]
    public async Task NestedManagedClass_CompilesWithoutSlotNameCollisions()
    {
        const string source = """
            using Paradise.ECS;
            namespace TestNamespace;
            public partial class First
            {
                [ManagedComponent] public sealed partial class Payload;
            }
            public partial class Second
            {
                [ManagedComponent] public sealed partial class Payload;
            }
            """;
        var (result, compilation) = GeneratorTestHelper.RunGeneratorsAndCompile(source, rootNamespace: "TestNamespace");
        await Assert.That(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(string.Join("\n", compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))).IsEmpty();
        var registry = GeneratedSource(result, "ComponentRegistry.g.cs");
        await Assert.That(registry).Contains("TestNamespace.First.Managed_Payload");
        await Assert.That(registry).Contains("TestNamespace.Second.Managed_Payload");
    }

    [Test]
    public async Task EscapedManagedAndContainingIdentifiers_Compile()
    {
        const string source = """
            using Paradise.ECS;
            namespace TestNamespace;
            public partial class @event
            {
                [ManagedComponent] public sealed partial class @class;
            }
            """;
        var (result, compilation) = GeneratorTestHelper.RunGeneratorsAndCompile(source, rootNamespace: "TestNamespace");
        await Assert.That(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(string.Join("\n", compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))).IsEmpty();
    }

    [Test]
    public async Task ManagedAndUnmanagedManualIdCollision_IsRejected()
    {
        const string source = """
            using Paradise.ECS;
            [Component(Id = 8)] public partial struct Position { public int Value; }
            [ManagedComponent(Id = 8)] public sealed partial class Payload;
            """;
        var result = GeneratorTestHelper.RunGenerator(source, includeManagedReference: true);
        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS015")).IsTrue();
        await Assert.That(result.GeneratedTrees.Any(t => Path.GetFileName(t.FilePath) == "ManagedRegistry.g.cs")).IsFalse();
    }

    [Test]
    public async Task ManagedAndUnmanagedGuidCollision_IsRejected()
    {
        const string source = """
            using Paradise.ECS;
            [Component, System.Runtime.InteropServices.Guid("c14c3e49-fb6b-487b-b352-cad14b446b80")]
            public partial struct Position { public int Value; }
            [ManagedComponent("C14C3E49-FB6B-487B-B352-CAD14B446B80")]
            public sealed partial class Payload;
            """;
        var (result, _) = GeneratorTestHelper.RunGeneratorsAndCompile(source);
        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS035")).IsTrue();
    }

    [Test]
    public async Task SparseManagedId_SizesAliasesAndQueryableMaskFromHighestId()
    {
        const string source = """
            using Paradise.ECS;
            namespace TestNamespace;
            [ManagedComponent(Id = 100)] public sealed partial class Payload;
            [Queryable, WithManaged<Payload>] public ref partial struct Items;
            """;
        var (result, compilation) = GeneratorTestHelper.RunGeneratorsAndCompile(source, rootNamespace: "TestNamespace");
        await Assert.That(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(string.Join("\n", compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))).IsEmpty();
        await Assert.That(GeneratedSource(result, "ComponentAliases.g.cs")).Contains("ImmutableBitSet<global::Paradise.ECS.Bit128>");
        await Assert.That(GeneratedSource(result, "ManagedRegistry.g.cs")).Contains("ImmutableBitSet<global::Paradise.ECS.Bit128>");
    }

    [Test]
    public async Task SuppressedAliases_PreservesManagedFactoryAndMetadata()
    {
        const string source = """
            using Paradise.ECS;
            [assembly: SuppressGlobalUsings]
            namespace TestNamespace;
            [ManagedComponent] public sealed partial class Payload;
            """;
        var (result, compilation) = GeneratorTestHelper.RunGeneratorsAndCompile(source, rootNamespace: "TestNamespace");
        await Assert.That(string.Join("\n", compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))).IsEmpty();
        await Assert.That(GeneratedSource(result, "ComponentAliases.g.cs")).DoesNotContain("global using");
        await Assert.That(GeneratedSource(result, "SharedWorldFactory.g.cs")).Contains("SharedManagedWorld");
        await Assert.That(GeneratedSource(result, "ManagedRegistry.g.cs")).Contains("TestNamespace.Payload");
    }

    [Test]
    public async Task GlobalNamespaceManagedClass_HasDistinctGeneratedHintNames()
    {
        const string source = """
            using Paradise.ECS;
            [ManagedComponent] public sealed partial class Payload;
            """;
        var (result, compilation) = GeneratorTestHelper.RunGeneratorsAndCompile(source);
        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(string.Join("\n", compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))).IsEmpty();
        await Assert.That(GeneratedSource(result, "Payload.ManagedComponent.g.cs")).Contains("IManagedComponent");
        await Assert.That(GeneratedSource(result, "Managed_Payload.g.cs")).Contains("IComponent");
    }

    [Test]
    public async Task UnrelatedEntityTagsType_DoesNotHideTheSyntheticComponentFromMaskSizing()
    {
        var components = string.Join("\n", Enumerable.Range(0, 30)
            .Select(i => $"[Component] public partial struct Position{i} {{ public int Value; }}"));
        var source = $$"""
            using Paradise.ECS;
            namespace Other { [Component] public partial struct EntityTags { public int Value; } }
            namespace TestNamespace
            {
                {{components}}
                [Tag] public partial struct Active;
                [ManagedComponent] public sealed partial class Payload;
                [Queryable, WithManaged<Payload>] public ref partial struct Items;
            }
            """;
        var (result, compilation) = GeneratorTestHelper.RunGeneratorsAndCompile(source, rootNamespace: "TestNamespace");
        await Assert.That(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(string.Join("\n", compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))).IsEmpty();
        await Assert.That(GeneratedSource(result, "ComponentAliases.g.cs")).Contains("SmallBitSet<ulong>");
        await Assert.That(GeneratedSource(result, "QueryableAliases.g.cs")).Contains("SmallBitSet<ulong>");
    }

    [Test]
    public async Task ReferencedManagedComponent_UsesItsOwningAssemblyRegistry()
    {
        const string librarySource = """
            using Paradise.ECS;
            namespace External;
            [ManagedComponent] public sealed partial class Payload;
            """;
        var (_, library) = GeneratorTestHelper.RunGeneratorsAndCompile(librarySource, rootNamespace: "External");
        library = library.WithAssemblyName("ManagedComponentLibrary");
        await Assert.That(string.Join("\n", library.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))).IsEmpty();
        const string consumerSource = """
            public static class Consumer
            {
                public static void Use()
                {
                    using var shared = External.SharedWorldFactory.Create();
                    var world = shared.CreateWorld();
                    var entity = world.Spawn();
                    world.AddManaged(entity, new External.Payload());
                    _ = world.GetManaged<External.Payload>(entity);
                }
            }
            """;
        var (result, consumer) = GeneratorTestHelper.RunGeneratorsAndCompile(consumerSource, rootNamespace: "ConsumerApp");
        consumer = consumer.AddReferences(library.ToMetadataReference());
        await Assert.That(string.Join("\n", consumer.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))).IsEmpty();
        await Assert.That(result.GeneratedTrees.Any(t => Path.GetFileName(t.FilePath) == "ManagedRegistry.g.cs")).IsFalse();
    }

    private static string GeneratedSource(GeneratorDriverRunResult result, string name)
        => result.GeneratedTrees.Single(t => Path.GetFileName(t.FilePath) == name).GetText().ToString();
}
