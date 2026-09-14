using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Paradise.ECS.Generators.Test;

public sealed class ManagedSystemGeneratorTests
{
    private const string Components = """
        using Paradise.ECS;
        namespace ManagedTests;
        [ManagedComponent] public sealed partial class Planner { }
        [ManagedComponent] public sealed partial class Catalog { }
        [ManagedComponent] public sealed partial class Disabled { }
        """;

    [Test]
    public async Task ManagedLookups_ContributeSlotAccessWithoutFilteringEntities()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Components + """

            public ref partial struct PlanningSystem : IWorldSystem
            {
                public ManagedLookup<Planner> Planners;
                public ReadOnlyManagedLookup<Catalog> Catalogs;
                public void Execute() { }
            }
            """, includeManagedReference: true);

        await Assert.That(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
        var registry = Source(result, "SystemRegistry.g.cs");
        await Assert.That(registry).Contains("readMask0 = TMask.Empty.Set(global::ManagedTests.Planner.SlotTypeId).Set(global::ManagedTests.Catalog.SlotTypeId);");
        await Assert.That(registry).Contains("writeMask0 = TMask.Empty.Set(global::ManagedTests.Planner.SlotTypeId);");
        await Assert.That(registry).Contains("allMask0 = TMask.Empty;");
        var system = Source(result, "System_ManagedTests_PlanningSystem.g.cs");
        await Assert.That(system).Contains("new global::Paradise.ECS.ManagedLookup<global::ManagedTests.Planner>((global::Paradise.ECS.IManagedWorld)world)");
    }

    [Test]
    public async Task SnapshotReaders_BindReadWorldAndCurrentTickReadersBindWriteWorld()
    {
        const string source = """
            using Paradise.ECS;
            [assembly: SnapshotReadSystems]
            namespace ManagedTests;
            [ManagedComponent] public sealed partial class Planner { }
            public ref partial struct PlanningSystem : IWorldSystem
            {
                public ReadOnlyManagedLookup<Planner> Previous;
                [CurrentTick] public ReadOnlyManagedLookup<Planner> Current;
                public void Execute() { }
            }
            """;
        var result = GeneratorTestHelper.RunSystemGenerator(source, includeManagedReference: true);

        await Assert.That(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
        var registry = Source(result, "SystemRegistry.g.cs");
        await Assert.That(registry).Contains("freshReadMask0 = TMask.Empty.Set(global::ManagedTests.Planner.SlotTypeId);");
        var system = Source(result, "System_ManagedTests_PlanningSystem.g.cs");
        await Assert.That(system).Contains("new global::Paradise.ECS.ReadOnlyManagedLookup<global::ManagedTests.Planner>((global::Paradise.ECS.IManagedWorld)(readWorld ?? world))");
        await Assert.That(system).Contains("new global::Paradise.ECS.ReadOnlyManagedLookup<global::ManagedTests.Planner>((global::Paradise.ECS.IManagedWorld)world)");
    }

    [Test]
    public async Task ManagedQueryable_UsesPresenceWithoutManagedDataClaims()
    {
        var (result, compilation) = GeneratorTestHelper.RunGeneratorsAndCompile(Components + """

            [Queryable, WithManaged<Planner>, WithoutManaged<Disabled>, WithManagedAny<Catalog>]
            public readonly ref partial struct Planned;
            public ref partial struct PlanningSystem : IEntitySystem
            {
                public Planned.Entity Presence;
                public ReadOnlyManagedLookup<Planner> Planners;
                public void Execute() { }
            }
            """, rootNamespace: "ManagedTests");

        await Assert.That(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(string.Join("\n", compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))).IsEqualTo("");
        var registry = Source(result, "SystemRegistry.g.cs");
        await Assert.That(registry).Contains("allMask0 = TMask.Empty.Set(global::ManagedTests.Planner.SlotTypeId);");
        await Assert.That(registry).Contains("noneMask0 = TMask.Empty.Set(global::ManagedTests.Disabled.SlotTypeId);");
        await Assert.That(registry).Contains("anyMask0 = TMask.Empty.Set(global::ManagedTests.Catalog.SlotTypeId);");
        await Assert.That(registry).Contains("writeMask0 = TMask.Empty;");
        var queryable = Source(result, "Queryable_ManagedTests_Planned.g.cs");
        await Assert.That(queryable).Contains("mask = mask.Set(global::ManagedTests.Planner.SlotTypeId);");
        await Assert.That(queryable).DoesNotContain("Span<global::ManagedTests.Planner>");
    }

    [Test]
    public async Task ManagedSystemFilters_UseSlotIds()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Components + """

            [WithManaged<Planner>, WithoutManaged<Disabled>, WithManagedAny<Catalog>]
            public ref partial struct PlanningSystem : IEntitySystem
            {
                public Entity Entity;
                public void Execute() { }
            }
            """, includeManagedReference: true);

        var registry = Source(result, "SystemRegistry.g.cs");
        await Assert.That(registry).Contains("allMask0 = TMask.Empty.Set(global::ManagedTests.Planner.SlotTypeId);");
        await Assert.That(registry).Contains("noneMask0 = TMask.Empty.Set(global::ManagedTests.Disabled.SlotTypeId);");
        await Assert.That(registry).Contains("anyMask0 = TMask.Empty.Set(global::ManagedTests.Catalog.SlotTypeId);");
    }

    [Test]
    [Arguments("Span")]
    [Arguments("ReadOnlySpan")]
    public async Task ManagedSpans_AreRejected(string spanType)
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Components + $$"""

            public ref partial struct PlanningSystem : IChunkSystem
            {
                public System.{{spanType}}<Planner> Planners;
                public void ExecuteChunk() { }
            }
            """, includeManagedReference: true);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS034")).IsTrue();
    }

    [Test]
    [Arguments("IEntitySystem")]
    [Arguments("IChunkSystem")]
    public async Task WritableLookup_RequiresWholeWorldDispatch(string systemInterface)
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Components + $$"""

            public ref partial struct PlanningSystem : {{systemInterface}}
            {
                public ManagedLookup<Planner> Planners;
                public void {{(systemInterface == "IChunkSystem" ? "ExecuteChunk" : "Execute")}}() { }
            }
            """, includeManagedReference: true);

        await Assert.That(result.Diagnostics.Any(d => d.GetMessage(CultureInfo.InvariantCulture).Contains("IWorldSystem", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.GeneratedTrees.Any(t => Path.GetFileName(t.FilePath).StartsWith("System_", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ManagedQueryableDataClaim_IsRejected()
    {
        var result = GeneratorTestHelper.RunQueryableGenerator(Components + """

            [Queryable, With<Planner>]
            public readonly ref partial struct Planned;
            """, includeManagedReference: true);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS034")).IsTrue();
    }

    [Test]
    public async Task ReservedSlotQueryable_IsRejected()
    {
        var result = GeneratorTestHelper.RunQueryableGenerator(Components + """

            [Queryable, With<Managed_Planner>]
            public readonly ref partial struct Planned;
            """, includeManagedReference: true);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS033")).IsTrue();
    }

    [Test]
    public async Task ContradictoryManagedPresence_IsRejected()
    {
        var result = GeneratorTestHelper.RunQueryableGenerator(Components + """

            [Queryable, WithManaged<Planner>, WithoutManaged<Planner>]
            public readonly ref partial struct Planned;
            """, includeManagedReference: true);

        await Assert.That(result.Diagnostics.Any(d => d.GetMessage(CultureInfo.InvariantCulture).Contains("WithManaged, WithoutManaged", StringComparison.Ordinal))).IsTrue();
    }

    private static string Source(GeneratorDriverRunResult result, string suffix) =>
        result.GeneratedTrees.Single(t => t.FilePath.EndsWith(suffix, StringComparison.Ordinal)).GetText().ToString();
}
