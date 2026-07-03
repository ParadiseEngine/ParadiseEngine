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
