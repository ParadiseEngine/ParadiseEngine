using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Paradise.ECS.Generators.Test;

public class SingleWriterAnalyzerTests
{
    private const string DiagnosticId = "PECS3008";

    private const string MarkedComponent = """
        using Paradise.ECS;

        [Component]
        [SingleWriter]
        public partial struct Position
        {
            public float X;
            public float Y;
        }
        """;

    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> Analyze(string source)
        => GeneratorTestHelper.GetAnalyzerDiagnosticsAsync<SingleWriterAnalyzer>(source, DiagnosticId);

    [Test]
    public async Task two_entity_systems_writing_a_marked_component_are_reported()
    {
        var diagnostics = await Analyze(MarkedComponent + """

            public ref partial struct MoveSystem : Paradise.ECS.IEntitySystem
            {
                public ref Position Position;
                public void Execute() { }
            }

            public ref partial struct BounceSystem : Paradise.ECS.IEntitySystem
            {
                public ref Position Position;
                public void Execute() { }
            }
            """);

        await Assert.That(diagnostics.Length).IsEqualTo(2); // one per writing field
        await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
        string message = diagnostics[0].GetMessage(CultureInfo.InvariantCulture);
        await Assert.That(message.Contains("'Position'", StringComparison.Ordinal)).IsTrue();
        await Assert.That(message.Contains("'BounceSystem'", StringComparison.Ordinal)).IsTrue();
        await Assert.That(message.Contains("'MoveSystem'", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task a_single_writer_is_allowed()
    {
        var diagnostics = await Analyze(MarkedComponent + """

            public ref partial struct MoveSystem : Paradise.ECS.IEntitySystem
            {
                public ref Position Position;
                public void Execute() { }
            }
            """);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ref_readonly_readers_do_not_count_as_writers()
    {
        var diagnostics = await Analyze(MarkedComponent + """

            public ref partial struct MoveSystem : Paradise.ECS.IEntitySystem
            {
                public ref Position Position;
                public void Execute() { }
            }

            public ref partial struct RenderSystem : Paradise.ECS.IEntitySystem
            {
                public ref readonly Position Position;
                public void Execute() { }
            }

            public ref partial struct AuditChunkSystem : Paradise.ECS.IChunkSystem
            {
                public System.ReadOnlySpan<Position> Positions;
                public void Execute() { }
            }
            """);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task unmarked_components_are_not_enforced()
    {
        var diagnostics = await Analyze("""
            using Paradise.ECS;

            [Component]
            public partial struct Velocity
            {
                public float X;
            }

            public ref partial struct GravitySystem : Paradise.ECS.IEntitySystem
            {
                public ref Velocity Velocity;
                public void Execute() { }
            }

            public ref partial struct DragSystem : Paradise.ECS.IEntitySystem
            {
                public ref Velocity Velocity;
                public void Execute() { }
            }
            """);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task a_chunk_system_span_counts_as_a_writer()
    {
        var diagnostics = await Analyze(MarkedComponent + """

            public ref partial struct MoveSystem : Paradise.ECS.IEntitySystem
            {
                public ref Position Position;
                public void Execute() { }
            }

            public ref partial struct BatchSystem : Paradise.ECS.IChunkSystem
            {
                public System.Span<Position> Positions;
                public void Execute() { }
            }
            """);

        await Assert.That(diagnostics.Length).IsEqualTo(2);
        string message = diagnostics[0].GetMessage(CultureInfo.InvariantCulture);
        await Assert.That(message.Contains("'BatchSystem'", StringComparison.Ordinal)).IsTrue();
        await Assert.That(message.Contains("'MoveSystem'", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task multiple_write_fields_in_one_system_are_a_single_writer()
    {
        var diagnostics = await Analyze(MarkedComponent + """

            public ref partial struct WeirdButLegalSystem : Paradise.ECS.IEntitySystem
            {
                public ref Position First;
                public ref Position Second;
                public void Execute() { }
            }
            """);

        await Assert.That(diagnostics.Length).IsEqualTo(0); // one system, however many fields
    }

    [Test]
    public async Task assembly_level_attribute_enforces_every_component_in_the_assembly()
    {
        var diagnostics = await Analyze("""
            using Paradise.ECS;

            [assembly: SingleWriter]

            [Component]
            public partial struct Velocity
            {
                public float X;
            }

            public ref partial struct GravitySystem : Paradise.ECS.IEntitySystem
            {
                public ref Velocity Velocity;
                public void Execute() { }
            }

            public ref partial struct DragSystem : Paradise.ECS.IEntitySystem
            {
                public ref Velocity Velocity;
                public void Execute() { }
            }
            """);

        await Assert.That(diagnostics.Length).IsEqualTo(2);
        string message = diagnostics[0].GetMessage(CultureInfo.InvariantCulture);
        await Assert.That(message.Contains("'Velocity'", StringComparison.Ordinal)).IsTrue();
        await Assert.That(message.Contains("'DragSystem'", StringComparison.Ordinal)).IsTrue();
        await Assert.That(message.Contains("'GravitySystem'", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task assembly_level_attribute_allows_a_single_writer_per_component()
    {
        var diagnostics = await Analyze("""
            using Paradise.ECS;

            [assembly: SingleWriter]

            [Component]
            public partial struct Velocity
            {
                public float X;
            }

            [Component]
            public partial struct Health
            {
                public float Value;
            }

            public ref partial struct GravitySystem : Paradise.ECS.IEntitySystem
            {
                public ref Velocity Velocity;
                public ref readonly Health Health;
                public void Execute() { }
            }

            public ref partial struct RegenSystem : Paradise.ECS.IEntitySystem
            {
                public ref Health Health;
                public void Execute() { }
            }
            """);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task assembly_level_attribute_ignores_structs_without_component_attribute()
    {
        var diagnostics = await Analyze("""
            using Paradise.ECS;

            [assembly: SingleWriter]

            public struct NotAComponent
            {
                public float X;
            }

            public ref partial struct FirstSystem : Paradise.ECS.IEntitySystem
            {
                public ref NotAComponent Value;
                public void Execute() { }
            }

            public ref partial struct SecondSystem : Paradise.ECS.IEntitySystem
            {
                public ref NotAComponent Value;
                public void Execute() { }
            }
            """);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task queryable_composition_fields_count_writable_with_components_as_writers()
    {
        var diagnostics = await Analyze(MarkedComponent + """

            [Queryable]
            [With<Position>]
            public readonly ref partial struct Movers;

            public ref partial struct MoveSystem : Paradise.ECS.IEntitySystem
            {
                public Movers Query;
                public void Execute() { }
            }

            public ref partial struct BounceSystem : Paradise.ECS.IEntitySystem
            {
                public Movers Query;
                public void Execute() { }
            }
            """);

        await Assert.That(diagnostics.Length).IsEqualTo(2);
        string message = diagnostics[0].GetMessage(CultureInfo.InvariantCulture);
        await Assert.That(message.Contains("'Position'", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task queryable_readonly_and_queryonly_with_components_are_not_writers()
    {
        var diagnostics = await Analyze(MarkedComponent + """

            [Queryable]
            [With<Position>(IsReadOnly = true)]
            public readonly ref partial struct Readers;

            [Queryable]
            [With<Position>(QueryOnly = true)]
            public readonly ref partial struct Filters;

            public ref partial struct MoveSystem : Paradise.ECS.IEntitySystem
            {
                public ref Position Position;
                public void Execute() { }
            }

            public ref partial struct ObserveSystem : Paradise.ECS.IEntitySystem
            {
                public Readers Query;
                public void Execute() { }
            }

            public ref partial struct FilterSystem : Paradise.ECS.IEntitySystem
            {
                public Filters Query;
                public void Execute() { }
            }
            """);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task queryable_writable_optional_components_count_as_writers()
    {
        var diagnostics = await Analyze(MarkedComponent + """

            [Queryable]
            [Optional<Position>]
            public readonly ref partial struct MaybeMovers;

            public ref partial struct MoveSystem : Paradise.ECS.IEntitySystem
            {
                public ref Position Position;
                public void Execute() { }
            }

            public ref partial struct OptionalWriterSystem : Paradise.ECS.IEntitySystem
            {
                public MaybeMovers Query;
                public void Execute() { }
            }
            """);

        await Assert.That(diagnostics.Length).IsEqualTo(2);
        string message = diagnostics[0].GetMessage(CultureInfo.InvariantCulture);
        await Assert.That(message.Contains("'OptionalWriterSystem'", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task queryable_readonly_optional_components_are_not_writers()
    {
        var diagnostics = await Analyze(MarkedComponent + """

            [Queryable]
            [Optional<Position>(IsReadOnly = true)]
            public readonly ref partial struct MaybeReaders;

            public ref partial struct MoveSystem : Paradise.ECS.IEntitySystem
            {
                public ref Position Position;
                public void Execute() { }
            }

            public ref partial struct OptionalReaderSystem : Paradise.ECS.IEntitySystem
            {
                public MaybeReaders Query;
                public void Execute() { }
            }
            """);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task singleton_composition_fields_count_writable_with_components_as_writers()
    {
        // Runs generators first so the {Prefix}Singleton alias resolves to the generated nested
        // Queryable.Singleton struct — the analyzer must see it through the SAME containing-type
        // path as existing composition fields.
        var diagnostics = await GeneratorTestHelper.GetAnalyzerDiagnosticsWithGeneratorsAsync<SingleWriterAnalyzer>(
            MarkedComponent + """

            [Queryable(Singleton = true)]
            [With<Position>]
            public readonly ref partial struct Board;

            public ref partial struct FirstBonusSystem : Paradise.ECS.IEntitySystem
            {
                public BoardSingleton Board;
                public void Execute() { }
            }

            public ref partial struct SecondBonusSystem : Paradise.ECS.IEntitySystem
            {
                public BoardSingleton Board;
                public void Execute() { }
            }
            """, DiagnosticId);

        await Assert.That(diagnostics.Length).IsEqualTo(2);
        string message = diagnostics[0].GetMessage(CultureInfo.InvariantCulture);
        await Assert.That(message.Contains("'Position'", StringComparison.Ordinal)).IsTrue();
        await Assert.That(message.Contains("'FirstBonusSystem'", StringComparison.Ordinal)).IsTrue();
        await Assert.That(message.Contains("'SecondBonusSystem'", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task current_tick_singleton_reader_with_one_writer_passes()
    {
        // [CurrentTick] reads fresh values but claims NO write — one real writer plus the
        // CurrentTick reader must not trip single-writer enforcement.
        var diagnostics = await GeneratorTestHelper.GetAnalyzerDiagnosticsWithGeneratorsAsync<SingleWriterAnalyzer>(
            MarkedComponent + """

            [Queryable(Singleton = true)]
            [With<Position>(IsReadOnly = true)]
            public readonly ref partial struct Observed;

            public ref partial struct MoveSystem : Paradise.ECS.IEntitySystem
            {
                public ref Position Position;
                public void Execute() { }
            }

            public ref partial struct FreshReaderSystem : Paradise.ECS.IEntitySystem
            {
                [Paradise.ECS.CurrentTick] public ObservedSingleton Observed;
                public void Execute() { }
            }
            """, DiagnosticId);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task non_system_ref_structs_are_ignored()
    {
        var diagnostics = await Analyze(MarkedComponent + """

            public ref partial struct MoveSystem : Paradise.ECS.IEntitySystem
            {
                public ref Position Position;
                public void Execute() { }
            }

            public ref struct NotASystem
            {
                public ref Position Position;
            }
            """);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }
}
