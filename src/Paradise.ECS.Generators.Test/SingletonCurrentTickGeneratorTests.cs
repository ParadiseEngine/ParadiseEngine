namespace Paradise.ECS.Generators.Test;

/// <summary>
/// Tests for [Queryable(Singleton = true)] type emission by the QueryableGenerator.
/// </summary>
public sealed class SingletonQueryableEmissionTests
{
    [Test]
    public async Task SingletonQueryable_EmitsSingletonStructAndAlias()
    {
        const string source = """
            using Paradise.ECS;

            namespace TestNamespace;

            [Component]
            public partial struct SimContext { public float DeltaTime; }

            [Queryable(Singleton = true)]
            [With<SimContext>(IsReadOnly = true)]
            public readonly ref partial struct GameState;
            """;

        var sources = GeneratorTestHelper.GetQueryableGeneratedSources(source);
        var generated = sources.FirstOrDefault(s => s.HintName == "Queryable_TestNamespace_GameState.g.cs").Source;

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!).Contains("public readonly ref struct Singleton<TMask, TConfig>");
        await Assert.That(generated).Contains("global using GameStateSingleton = global::TestNamespace.GameState.Singleton<");
        // Resolution: query the write world, assert exactly one entity, pair against the read world
        await Assert.That(generated).Contains("public static Singleton<TMask, TConfig> Resolve(");
        await Assert.That(generated).Contains("Singleton queryable 'TestNamespace.GameState' must resolve to exactly one entity");
        await Assert.That(generated).Contains("SnapshotChunkPairing.Resolve(world, readWorld");
        // Component access honors IsReadOnly, exactly like Data
        await Assert.That(generated).Contains("public ref readonly global::TestNamespace.SimContext SimContext");
    }

    [Test]
    public async Task NonSingletonQueryable_EmitsNoSingletonType()
    {
        const string source = """
            using Paradise.ECS;

            namespace TestNamespace;

            [Component]
            public partial struct SimContext { public float DeltaTime; }

            [Queryable]
            [With<SimContext>]
            public readonly ref partial struct GameState;
            """;

        var sources = GeneratorTestHelper.GetQueryableGeneratedSources(source);
        var generated = sources.FirstOrDefault(s => s.HintName == "Queryable_TestNamespace_GameState.g.cs").Source;

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!).DoesNotContain("struct Singleton<");
        await Assert.That(generated).DoesNotContain("global using GameStateSingleton");
    }

    [Test]
    public async Task SingletonQueryable_WritableComponent_ExposesWritableRef()
    {
        const string source = """
            using Paradise.ECS;

            namespace TestNamespace;

            [Component]
            public partial struct Score { public int Total; }

            [Queryable(Singleton = true)]
            [With<Score>]
            public readonly ref partial struct Board;
            """;

        var sources = GeneratorTestHelper.GetQueryableGeneratedSources(source);
        var generated = sources.FirstOrDefault(s => s.HintName == "Queryable_TestNamespace_Board.g.cs").Source;

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!).Contains("public ref global::TestNamespace.Score Score");
    }
}

/// <summary>
/// Tests for {Prefix}Singleton field recognition and codegen in the SystemGenerator.
/// </summary>
public sealed class SingletonSystemFieldTests
{
    private const string SingletonPreamble = """
        using Paradise.ECS;

        namespace TestNamespace;

        [Component]
        public partial struct SimContext { public float DeltaTime; }

        [Component]
        public partial struct Position { public float X; }

        [Queryable(Singleton = true)]
        [With<SimContext>(IsReadOnly = true)]
        public readonly ref partial struct GameState;

        """;

    [Test]
    public async Task EntitySystem_SingletonField_ResolvesOncePerDispatch()
    {
        var generated = GeneratorTestHelper.GetSystemGeneratedSource(SingletonPreamble + """
            public ref partial struct MoveSystem : IEntitySystem
            {
                public ref Position Position;
                public GameStateSingleton State;
                public void Execute() { }
            }
            """, "System_TestNamespace_MoveSystem.g.cs");

        await Assert.That(generated).IsNotNull();
        // Constructor takes the generated Singleton composition type
        await Assert.That(generated!).Contains("global::TestNamespace.GameState.Singleton<");
        // Classic codegen: resolved before iterating, bound to the write world (null read world)
        await Assert.That(generated).Contains("var stateSingleton = global::TestNamespace.GameState.Singleton<");
        await Assert.That(generated).Contains(".Resolve(world, null);");
        await Assert.That(generated).Contains("stateSingleton");
    }

    [Test]
    public async Task ChunkSystem_SingletonField_ResolvesOncePerDispatch()
    {
        var generated = GeneratorTestHelper.GetSystemGeneratedSource(SingletonPreamble + """
            public ref partial struct BatchSystem : IChunkSystem
            {
                public System.Span<Position> Positions;
                public GameStateSingleton State;
                public void ExecuteChunk() { }
            }
            """, "System_TestNamespace_BatchSystem.g.cs");

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!).Contains("var stateSingleton = global::TestNamespace.GameState.Singleton<");
        await Assert.That(generated).Contains(".Resolve(world, null);");
    }

    [Test]
    public async Task WorldSystem_SingletonField_IsAccepted()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(SingletonPreamble + """
            [Queryable]
            [With<Position>]
            public readonly ref partial struct Movers;

            public ref partial struct GlobalSystem : IWorldSystem
            {
                public MoversSegments Movers;
                public GameStateSingleton State;
                public void Execute() { }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3009")).IsFalse();
        var generated = result.GeneratedTrees
            .Select(t => (HintName: System.IO.Path.GetFileName(t.FilePath), Source: t.GetText().ToString()))
            .FirstOrDefault(s => s.HintName == "System_TestNamespace_GlobalSystem.g.cs").Source;
        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!).Contains("var stateSingleton = global::TestNamespace.GameState.Singleton<");
    }

    [Test]
    public async Task SingletonComponents_FlowIntoReadMask_ButNotQueryMask()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(SingletonPreamble + """
            public ref partial struct MoveSystem : IEntitySystem
            {
                public ref Position Position;
                public GameStateSingleton State;
                public void Execute() { }
            }
            """);
        var registrySource = result.GeneratedTrees
            .Select(t => (HintName: System.IO.Path.GetFileName(t.FilePath), Source: t.GetText().ToString()))
            .FirstOrDefault(s => s.HintName == "SystemRegistry.g.cs").Source;

        await Assert.That(registrySource).IsNotNull();
        // SimContext is read via the singleton — in the read mask for scheduling…
        await Assert.That(registrySource!).Contains("SimContext.TypeId");
        // …but the system's own QUERY only matches Position (the singleton is not a filter)
        await Assert.That(registrySource).Contains("allMask0 = TMask.Empty.Set(global::TestNamespace.Position.TypeId);");
        // Read-only singleton component never claims a write
        await Assert.That(registrySource).Contains("writeMask0 = TMask.Empty.Set(global::TestNamespace.Position.TypeId);");
    }

    [Test]
    public async Task WritableSingletonComponent_FlowsIntoWriteMask()
    {
        var result = GeneratorTestHelper.RunSystemGenerator("""
            using Paradise.ECS;

            namespace TestNamespace;

            [Component]
            public partial struct Score { public int Total; }

            [Queryable(Singleton = true)]
            [With<Score>]
            public readonly ref partial struct Board;

            public ref partial struct BonusSystem : IEntitySystem
            {
                public BoardSingleton Board;
                public void Execute() { }
            }
            """);
        var registrySource = result.GeneratedTrees
            .Select(t => (HintName: System.IO.Path.GetFileName(t.FilePath), Source: t.GetText().ToString()))
            .FirstOrDefault(s => s.HintName == "SystemRegistry.g.cs").Source;

        await Assert.That(registrySource).IsNotNull();
        await Assert.That(registrySource!).Contains("readMask0 = TMask.Empty.Set(global::TestNamespace.Score.TypeId);");
        await Assert.That(registrySource).Contains("writeMask0 = TMask.Empty.Set(global::TestNamespace.Score.TypeId);");
        await Assert.That(registrySource).Contains("allMask0 = TMask.Empty;");
    }

    [Test]
    public async Task SingletonField_OnNonSingletonQueryable_ReportsPECS3010()
    {
        var result = GeneratorTestHelper.RunSystemGenerator("""
            using Paradise.ECS;

            namespace TestNamespace;

            [Component]
            public partial struct SimContext { public float DeltaTime; }

            [Queryable]
            [With<SimContext>(IsReadOnly = true)]
            public readonly ref partial struct GameState;

            public ref partial struct MoveSystem : IEntitySystem
            {
                public GameStateSingleton State;
                public void Execute() { }
            }
            """);

        var diagnostic = result.Diagnostics.FirstOrDefault(d => d.Id == "PECS3010");
        await Assert.That(diagnostic).IsNotNull();
        var message = diagnostic!.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        await Assert.That(message).Contains("GameState");
        await Assert.That(message).Contains("Singleton = true");
    }

    [Test]
    public async Task SnapshotCodegen_SingletonBindsReadWorld_CurrentTickBindsWriteWorld()
    {
        var generated = GeneratorTestHelper.GetSystemGeneratedSource("""
            using Paradise.ECS;

            [assembly: SnapshotReadSystems]

            namespace TestNamespace;

            [Component]
            public partial struct SimContext { public float DeltaTime; }

            [Component]
            public partial struct Position { public float X; }

            [Queryable(Singleton = true)]
            [With<SimContext>(IsReadOnly = true)]
            public readonly ref partial struct GameState;

            public ref partial struct StaleSystem : IEntitySystem
            {
                public ref Position Position;
                public GameStateSingleton State;
                public void Execute() { }
            }
            """, "System_TestNamespace_StaleSystem.g.cs");

        await Assert.That(generated).IsNotNull();
        // Snapshot codegen without [CurrentTick]: read-only singleton components pair with the READ world
        await Assert.That(generated!).Contains(".Resolve(world, readWorld);");

        var freshGenerated = GeneratorTestHelper.GetSystemGeneratedSource("""
            using Paradise.ECS;

            [assembly: SnapshotReadSystems]

            namespace TestNamespace;

            [Component]
            public partial struct SimContext { public float DeltaTime; }

            [Component]
            public partial struct Position { public float X; }

            [Queryable(Singleton = true)]
            [With<SimContext>(IsReadOnly = true)]
            public readonly ref partial struct GameState;

            public ref partial struct FreshSystem : IEntitySystem
            {
                public ref Position Position;
                [CurrentTick] public GameStateSingleton State;
                public void Execute() { }
            }
            """, "System_TestNamespace_FreshSystem.g.cs");

        await Assert.That(freshGenerated).IsNotNull();
        // [CurrentTick]: everything binds to the write world (null read world)
        await Assert.That(freshGenerated!).Contains(".Resolve(world, null);");
        await Assert.That(freshGenerated).DoesNotContain(".Resolve(world, readWorld);");
    }
}

/// <summary>
/// Tests for [CurrentTick] field validation and fresh-read mask emission.
/// </summary>
public sealed class CurrentTickFieldTests
{
    private const string Components = """
        using Paradise.ECS;

        namespace TestNamespace;

        [Component]
        public partial struct Position { public float X; }

        [Component]
        public partial struct Marker { public float Observed; }

        """;

    [Test]
    public async Task CurrentTick_OnReadOnlyInlineField_EmitsFreshReadMask()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Components + """
            public ref partial struct FreshReaderSystem : IEntitySystem
            {
                [CurrentTick] public ref readonly Position Position;
                public ref Marker Marker;
                public void Execute() { }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3011")).IsFalse();
        var registrySource = result.GeneratedTrees
            .Select(t => (HintName: System.IO.Path.GetFileName(t.FilePath), Source: t.GetText().ToString()))
            .FirstOrDefault(s => s.HintName == "SystemRegistry.g.cs").Source;
        await Assert.That(registrySource).IsNotNull();
        await Assert.That(registrySource!).Contains("freshReadMask0 = TMask.Empty.Set(global::TestNamespace.Position.TypeId);");
        await Assert.That(registrySource).Contains("FreshReadMask = freshReadMask0,");
        // No write claim: Position stays out of the write mask
        await Assert.That(registrySource).Contains("writeMask0 = TMask.Empty.Set(global::TestNamespace.Marker.TypeId);");
    }

    [Test]
    public async Task CurrentTick_OnCurrentTickSingleton_EmitsFreshReadMask()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Components + """
            [Queryable(Singleton = true)]
            [With<Position>(IsReadOnly = true)]
            public readonly ref partial struct GameState;

            public ref partial struct FreshReaderSystem : IEntitySystem
            {
                [CurrentTick] public GameStateSingleton State;
                public ref Marker Marker;
                public void Execute() { }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3011")).IsFalse();
        var registrySource = result.GeneratedTrees
            .Select(t => (HintName: System.IO.Path.GetFileName(t.FilePath), Source: t.GetText().ToString()))
            .FirstOrDefault(s => s.HintName == "SystemRegistry.g.cs").Source;
        await Assert.That(registrySource).IsNotNull();
        await Assert.That(registrySource!).Contains("freshReadMask0 = TMask.Empty.Set(global::TestNamespace.Position.TypeId);");
    }

    [Test]
    public async Task CurrentTick_WithoutSnapshotCodegen_IsANoOp()
    {
        // Classic codegen: reads already bind to the (single) write world — allowed, no readBytes
        var generated = GeneratorTestHelper.GetSystemGeneratedSource(Components + """
            public ref partial struct FreshReaderSystem : IEntitySystem
            {
                [CurrentTick] public ref readonly Position Position;
                public ref Marker Marker;
                public void Execute() { }
            }
            """, "System_TestNamespace_FreshReaderSystem.g.cs");

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!).Contains("bytes.GetSpan<global::TestNamespace.Position>");
        await Assert.That(generated).DoesNotContain("readBytes");
    }

    [Test]
    public async Task CurrentTick_SnapshotCodegen_BindsInlineFieldToWriteWorld()
    {
        var generated = GeneratorTestHelper.GetSystemGeneratedSource("""
            using Paradise.ECS;

            [assembly: SnapshotReadSystems]

            namespace TestNamespace;

            [Component]
            public partial struct Position { public float X; }

            [Component]
            public partial struct Marker { public float Observed; }

            public ref partial struct FreshReaderSystem : IEntitySystem
            {
                [CurrentTick] public ref readonly Position Position;
                public ref Marker Marker;
                public void Execute() { }
            }
            """, "System_TestNamespace_FreshReaderSystem.g.cs");

        await Assert.That(generated).IsNotNull();
        // The [CurrentTick] read-only span comes from the WRITE chunk's bytes, not readBytes
        await Assert.That(generated!).Contains("var positionSpan = bytes.GetSpan<global::TestNamespace.Position>");
        await Assert.That(generated).DoesNotContain("readBytes");
    }

    [Test]
    public async Task CurrentTick_OnWritableRefField_ReportsPECS3011()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Components + """
            public ref partial struct BadSystem : IEntitySystem
            {
                [CurrentTick] public ref Position Position;
                public void Execute() { }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3011")).IsTrue();
    }

    [Test]
    public async Task CurrentTick_OnSpanField_ReportsPECS3011()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Components + """
            public ref partial struct BadChunkSystem : IChunkSystem
            {
                [CurrentTick] public System.ReadOnlySpan<Position> Positions;
                public void ExecuteChunk() { }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3011")).IsTrue();
    }

    [Test]
    public async Task CurrentTick_OnCompositionDataField_ReportsPECS3011()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Components + """
            [Queryable]
            [With<Position>(IsReadOnly = true)]
            public readonly ref partial struct Readers;

            public ref partial struct BadCompositionSystem : IEntitySystem
            {
                [CurrentTick] public ReadersEntity Query;
                public void Execute() { }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3011")).IsTrue();
    }

    [Test]
    public async Task CurrentTick_OnCommandBufferField_ReportsPECS3011()
    {
        var result = GeneratorTestHelper.RunSystemGenerator(Components + """
            public ref partial struct BadEcbSystem : IEntitySystem
            {
                [CurrentTick] public EntityCommandBuffer Commands;
                public ref Marker Marker;
                public void Execute() { }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PECS3011")).IsTrue();
    }
}
