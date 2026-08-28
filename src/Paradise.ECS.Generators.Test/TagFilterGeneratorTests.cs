namespace Paradise.ECS.Generators.Test;

/// <summary>
/// [WithTag]/[WithoutTag] row filters, PECS017 (same tag required and excluded), and PECS3012
/// (Chunk/Segments of a tag-filtered queryable) plus [IgnoreTags] as the field-level opt-out.
/// </summary>
public sealed class TagFilterGeneratorTests
{
    private const string Preamble = """
        using Paradise.ECS;

        namespace TestNamespace;

        [Component]
        public partial struct Position { public float X; }

        [Tag]
        public partial struct Alive;
        """;

    [Test]
    public async Task WithoutTag_GeneratesInvertedRowTestAndEntityTagsRequirement()
    {
        const string source = Preamble + """

            [Queryable]
            [WithoutTag<Alive>]
            [With<Position>]
            public readonly ref partial struct DeadOrIdle;
            """;

        var sources = GeneratorTestHelper.GetQueryableGeneratedSources(source);
        var generated = sources.FirstOrDefault(s =>
            s.HintName.Contains("DeadOrIdle", StringComparison.Ordinal)).Source;

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!).Contains("!__tags.Mask.Get(global::TestNamespace.Alive.TagId)");
        await Assert.That(generated).Contains("Set(global::Paradise.ECS.EntityTags.TypeId)");
        // WithoutTag cannot skip a chunk: ChunkMatches is omitted (interface default is true).
        await Assert.That(generated).DoesNotContain("public static bool ChunkMatches(");
    }

    [Test]
    public async Task WithTagAndWithoutTag_DifferentTags_BothAppearInMatches()
    {
        const string source = Preamble + """

            [Tag]
            public partial struct Hostile;

            [Queryable]
            [WithTag<Alive>]
            [WithoutTag<Hostile>]
            [With<Position>]
            public readonly ref partial struct Friendly;
            """;

        var sources = GeneratorTestHelper.GetQueryableGeneratedSources(source);
        var generated = sources.FirstOrDefault(s =>
            s.HintName.Contains("Friendly", StringComparison.Ordinal)).Source;

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!).Contains("__tags.Mask.Get(global::TestNamespace.Alive.TagId)");
        await Assert.That(generated).Contains("!__tags.Mask.Get(global::TestNamespace.Hostile.TagId)");
        await Assert.That(generated).Contains("public static bool ChunkMatches(");
        await Assert.That(generated).Contains("__chunkTags.Mask.Get(global::TestNamespace.Alive.TagId)");
        await Assert.That(generated).DoesNotContain("__chunkTags.Mask.Get(global::TestNamespace.Hostile.TagId)");
    }

    [Test]
    public async Task WithTagAndWithoutTag_SameTag_IsPecs017()
    {
        const string source = Preamble + """

            [Queryable]
            [WithTag<Alive>]
            [WithoutTag<Alive>]
            [With<Position>]
            public readonly ref partial struct Impossible;
            """;

        var diagnostics = GeneratorTestHelper.GetQueryableDiagnostics(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PECS017")).IsTrue();

        var sources = GeneratorTestHelper.GetQueryableGeneratedSources(source);
        await Assert.That(sources.Any(s => s.HintName.Contains("Impossible", StringComparison.Ordinal)))
            .IsFalse();
    }

    [Test]
    public async Task WorldSystem_SegmentsOfTaggedQueryable_IsPecs3012()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Preamble + """

            [Queryable]
            [WithTag<Alive>]
            [With<Position>]
            public readonly ref partial struct Living;

            public ref partial struct Tick : IWorldSystem
            {
                public Living.Segments Bodies;
                public void Execute() { }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3012")).IsTrue();
    }

    [Test]
    public async Task ChunkSystem_ChunkOfTaggedQueryable_IsPecs3012()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Preamble + """

            [Queryable]
            [WithoutTag<Alive>]
            [With<Position>]
            public readonly ref partial struct Corpses;

            public ref partial struct Tick : IChunkSystem
            {
                public Corpses.Chunk Rows;
                public void Execute() { }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3012")).IsTrue();
    }

    [Test]
    public async Task IgnoreTagsOnField_AllowsSegmentsOfTaggedQueryable()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Preamble + """

            [Queryable]
            [WithTag<Alive>]
            [With<Position>]
            public readonly ref partial struct Living;

            public ref partial struct Tick : IWorldSystem
            {
                [IgnoreTags]
                public Living.Segments Bodies;
                public void Execute() { }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3012")).IsFalse();
        var registry = result.GeneratedTrees
            .Select(t => System.IO.Path.GetFileName(t.FilePath))
            .Any(n => n == "SystemRegistry.g.cs");
        await Assert.That(registry).IsTrue();
    }

    [Test]
    public async Task IgnoreTagsOnLookup_IsAllowed()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Preamble + """

            [Queryable]
            [WithTag<Alive>]
            [With<Position>]
            public readonly ref partial struct Living;

            public ref partial struct Tick : IWorldSystem
            {
                [IgnoreTags]
                public Living.ReadLookup Bodies;
                public void Execute() { }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3013")).IsFalse();
        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3012")).IsFalse();
        var generated = result.GeneratedTrees
            .Select(t => (HintName: System.IO.Path.GetFileName(t.FilePath), Source: t.GetText().ToString()))
            .FirstOrDefault(s => s.HintName == "System_TestNamespace_Tick.g.cs").Source;
        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!).Contains("ignoreTags: true");
    }

    [Test]
    public async Task IgnoreTagsOnEntity_OmitsRowMatches()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Preamble + """

            [Queryable]
            [WithTag<Alive>]
            [With<Position>]
            public readonly ref partial struct Living;

            public ref partial struct Tick : IEntitySystem
            {
                [IgnoreTags]
                public Living.Entity Row;
                public void Execute() { }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3013")).IsFalse();
        var generated = result.GeneratedTrees
            .Select(t => (HintName: System.IO.Path.GetFileName(t.FilePath), Source: t.GetText().ToString()))
            .FirstOrDefault(s => s.HintName == "System_TestNamespace_Tick.g.cs").Source;
        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!).DoesNotContain("RowMatches");
    }

    [Test]
    public async Task IgnoreTagsOnSingleton_PassesTrueToResolve()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Preamble + """

            [Queryable(Singleton = true)]
            [WithTag<Alive>]
            [With<Position>]
            public readonly ref partial struct TheOne;

            public ref partial struct Tick : IWorldSystem
            {
                [IgnoreTags]
                public TheOne.Singleton Body;
                public void Execute() { }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3013")).IsFalse();
        var generated = result.GeneratedTrees
            .Select(t => (HintName: System.IO.Path.GetFileName(t.FilePath), Source: t.GetText().ToString()))
            .FirstOrDefault(s => s.HintName == "System_TestNamespace_Tick.g.cs").Source;
        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!).Contains("ignoreTags: true");
    }

    [Test]
    public async Task IgnoreTagsOnCommandBuffer_IsPecs3013()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Preamble + """

            public ref partial struct Tick : IWorldSystem
            {
                [IgnoreTags]
                public EntityCommandBuffer Commands;
                public void Execute() { }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3013")).IsTrue();
    }

    [Test]
    public async Task IgnoreTagsOnChunk_AllowsChunkOfTaggedQueryable()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Preamble + """

            [Queryable]
            [WithTag<Alive>]
            [With<Position>]
            public readonly ref partial struct Living;

            public ref partial struct Tick : IChunkSystem
            {
                [IgnoreTags]
                public Living.Chunk Rows;
                public void Execute() { }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3012")).IsFalse();
        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3013")).IsFalse();
    }

    [Test]
    public async Task LookupOfTaggedQueryable_IsNotPecs3012()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Preamble + """

            [Queryable]
            [WithTag<Alive>]
            [With<Position>]
            public readonly ref partial struct Living;

            public ref partial struct Tick : IWorldSystem
            {
                public Living.ReadLookup Bodies;
                public void Execute() { }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3012")).IsFalse();
        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3009")).IsFalse();
    }

    [Test]
    public async Task EntityClaimOfTaggedQueryable_IsNotPecs3012()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Preamble + """

            [Queryable]
            [WithTag<Alive>]
            [With<Position>]
            public readonly ref partial struct Living;

            public ref partial struct Tick : IEntitySystem
            {
                public Living.Entity Row;
                public void Execute() { }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3012")).IsFalse();
        var generated = result.GeneratedTrees
            .Select(t => (HintName: System.IO.Path.GetFileName(t.FilePath), Source: t.GetText().ToString()))
            .FirstOrDefault(s => s.HintName == "System_TestNamespace_Tick.g.cs").Source;
        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!).Contains("RowMatches");
    }
}
