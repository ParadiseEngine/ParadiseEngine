namespace Paradise.ECS.Generators.Test;

public sealed class EntityComponentAccessGeneratorTests
{
    private const string Source = """
        using Paradise.ECS;

        namespace TestNamespace;

        [Component]
        public partial struct Position { public float X; }

        [Component]
        public partial struct Source { public Entity Target; }

        public ref partial struct ReaderSystem : IEntitySystem
        {
            public ref Source Source;
            public EntityComponentReader<Position> Position;

            public void Execute()
            {
                if (Position.TryGet(Source.Target, out var position))
                    Source.Target = default;
            }
        }

        public ref partial struct WriterSystem : IEntitySystem
        {
            public ref Source Source;
            public EntityComponentWriter<Position> TargetPosition;

            public void Execute()
            {
                TargetPosition.Set(Source.Target, default);
            }
        }
        """;

    [Test]
    public async Task entity_reader_generates_dependency_without_query_filter()
    {
        var generated = GeneratorTestHelper.GetSystemGeneratedSource(Source, "System_TestNamespace_ReaderSystem.g.cs");

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!).Contains("global::Paradise.ECS.EntityComponentReader<global::TestNamespace.Position>");
        await Assert.That(generated).Contains("new global::Paradise.ECS.EntityComponentReader<global::TestNamespace.Position>(world)");
    }

    [Test]
    public async Task entity_writer_generates_dependency_without_query_filter()
    {
        var generated = GeneratorTestHelper.GetSystemGeneratedSource(Source, "System_TestNamespace_WriterSystem.g.cs");

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!).Contains("global::Paradise.ECS.EntityComponentWriter<global::TestNamespace.Position>");
        await Assert.That(generated).Contains("new global::Paradise.ECS.EntityComponentWriter<global::TestNamespace.Position>(world)");
    }
}
