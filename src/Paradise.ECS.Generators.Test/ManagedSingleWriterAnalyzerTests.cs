namespace Paradise.ECS.Generators.Test;

public sealed class ManagedSingleWriterAnalyzerTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ManagedWriters_RespectComponentAndAssemblySingleWriter(bool assemblyWide)
    {
        string source = $$"""
            using Paradise.ECS;
            {{(assemblyWide ? "[assembly: SingleWriter]" : "")}}
            [ManagedComponent{{(assemblyWide ? "" : ", SingleWriter")}}]
            public sealed partial class Planner { }
            public ref partial struct First : IWorldSystem
            {
                public ManagedLookup<Planner> Planners;
                public void Execute() { }
            }
            public ref partial struct Second : IWorldSystem
            {
                public ManagedLookup<Planner> Planners;
                public void Execute() { }
            }
            """;

        var diagnostics = await GeneratorTestHelper.GetAnalyzerDiagnosticsAsync<SingleWriterAnalyzer>(source, "PECS3008");

        await Assert.That(diagnostics.Length).IsEqualTo(2);
    }

    [Test]
    public async Task ManagedReadOnlyLookup_DoesNotCountAsWriter()
    {
        const string source = """
            using Paradise.ECS;
            [assembly: SingleWriter]
            [ManagedComponent] public sealed partial class Planner { }
            public ref partial struct First : IWorldSystem
            {
                public ManagedLookup<Planner> Planners;
                public void Execute() { }
            }
            public ref partial struct Second : IWorldSystem
            {
                public ReadOnlyManagedLookup<Planner> Planners;
                public void Execute() { }
            }
            """;

        var diagnostics = await GeneratorTestHelper.GetAnalyzerDiagnosticsAsync<SingleWriterAnalyzer>(source, "PECS3008");

        await Assert.That(diagnostics).IsEmpty();
    }
}
