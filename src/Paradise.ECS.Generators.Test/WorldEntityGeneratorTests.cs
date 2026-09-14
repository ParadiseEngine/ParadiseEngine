using Microsoft.CodeAnalysis;

namespace Paradise.ECS.Generators.Test;

public class WorldEntityGeneratorTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task WorldEntity_CompilesUnifiedAccessAcrossWorldCompositions(bool tags, bool managed)
    {
        var source = $$"""
            using Paradise.ECS;
            namespace TestNamespace;
            [Component] public partial struct Position { public int Value; }
            {{(tags ? "[Tag] public partial struct Active;" : "")}}
            {{(managed ? "[ManagedComponent] public sealed partial class Payload { public string Text = string.Empty; }" : "")}}
            public static class Consumer
            {
                public static void Use()
                {
                    using var shared = SharedWorldFactory.Create();
                    var world = shared.CreateWorld();
                    WorldEntity reference = new(world, world.Spawn());
                    var stored = new WorldEntity[] { reference };
                    reference.Add<Position>();
                    Position original = reference.Get<Position>();
                    reference.Set(new Position { Value = original.Value + 1 });
                    _ = reference.Has<Position>();
                    _ = reference.TryGet<Position>(out Position value);
                    ref Position current = ref reference.GetRef<Position>();
                    current.Value = value.Value + 1;
                    reference.Remove<Position>();
                    stored[0].Add(new Position { Value = 5 });
                    {{(tags ? "reference.Add<Active>(); _ = reference.Has<Active>(); reference.Remove<Active>();" : "")}}
                    {{(managed ? """
                        reference.Add<Payload>();
                        Payload? absent = reference.Get<Payload>();
                        reference.Set<Payload>(null);
                        _ = reference.Has<Payload>();
                        _ = reference.TryGet<Payload>(out Payload? payload);
                        reference.Remove<Payload>();
                        reference.Add(new Payload { Text = "value" });
                        """ : "")}}
                    EntityQueryResult entities = QueryBuilder.Create().Build(world);
                    EntityChunkQueryResult chunks = QueryBuilder.Create().BuildChunk(world);
                    _ = entities;
                    _ = chunks;
                }
            }
            """;
        var (result, compilation) = GeneratorTestHelper.RunGeneratorsAndCompile(source, rootNamespace: "TestNamespace",
            includeTagReference: tags, includeManagedReference: managed);
        await AssertCompiles(result, compilation);
        var component = GeneratedSource(result, "TestNamespace_Position.g.cs");
        await Assert.That(component).Contains("IEntityComponent<global::TestNamespace.Position>");
        await Assert.That(component).Contains("static void global::Paradise.ECS.IEntityComponent.Add");
        await Assert.That(component).Contains("world.AddComponent<global::TestNamespace.Position>(entity)");
        if (managed)
        {
            var payload = GeneratedSource(result, "TestNamespace_Payload.ManagedComponent.g.cs");
            await Assert.That(payload).Contains("IEntityComponent<global::TestNamespace.Payload>");
            await Assert.That(payload).Contains("world.GetExtension<global::Paradise.ECS.IManagedWorld>()");
            await Assert.That(payload).Contains("global::TestNamespace.Payload? value");
            await Assert.That(GeneratedSource(result, "TestNamespace_Managed_Payload.g.cs")).DoesNotContain("IEntityComponent");
        }
        if (tags)
        {
            var tag = GeneratedSource(result, "Tag_TestNamespace_Active.g.cs");
            await Assert.That(tag).Contains("global::Paradise.ECS.IEntityComponent");
            await Assert.That(tag).Contains("world.GetExtension<global::Paradise.ECS.ITagWorld>()");
            await Assert.That(tag).DoesNotContain("IEntityComponent<");
        }
    }

    [Test]
    public async Task NestedTypesAndSuppressedAliases_CompileWithStableWorldEntity()
    {
        const string source = """
            using Paradise.ECS;
            [assembly: SuppressGlobalUsings]
            namespace TestNamespace;
            public partial class Domain
            {
                [Component] public partial struct Position { public int Value; }
                [Tag] public partial struct Active;
                [ManagedComponent] public sealed partial class Payload;
            }
            public static class Consumer
            {
                public static void Use()
                {
                    using var shared = SharedWorldFactory.Create();
                    var world = shared.CreateWorld();
                    var reference = new WorldEntity(world, world.Spawn());
                    reference.Add<Domain.Position>();
                    reference.Add<Domain.Active>();
                    reference.Add<Domain.Payload>();
                    reference.Set(new Domain.Position { Value = 8 });
                    reference.Set<Domain.Payload>(null);
                }
            }
            """;
        var (result, compilation) = GeneratorTestHelper.RunGeneratorsAndCompile(source, rootNamespace: "TestNamespace");
        await AssertCompiles(result, compilation);
        await Assert.That(GeneratedSource(result, "ComponentAliases.g.cs")).DoesNotContain("global using");
    }

    [Test]
    public async Task GlobalNamespaceTypes_CompileStaticDispatch()
    {
        const string source = """
            using Paradise.ECS;
            [Component] public partial struct Position { public int Value; }
            [Tag] public partial struct Active;
            [ManagedComponent] public sealed partial class Payload;
            public static class Consumer
            {
                public static void Use(WorldEntity reference)
                {
                    reference.Add<Position>();
                    reference.Add<Active>();
                    reference.Add<Payload>();
                }
            }
            """;
        var (result, compilation) = GeneratorTestHelper.RunGeneratorsAndCompile(source);
        await AssertCompiles(result, compilation);
    }

    [Test]
    public async Task AuthoredStaticMethods_DoNotCollideWithExplicitGeneratedDispatch()
    {
        const string source = """
            using Paradise.ECS;
            namespace TestNamespace;
            [Component] public partial struct Position
            {
                public int Value;
                public static void Add(IWorld world, Entity entity) { }
                public static bool Has(IWorld world, Entity entity) => false;
                public static Position Get(IWorld world, Entity entity) => default;
            }
            [Tag] public partial struct Active
            {
                public static void Remove(IWorld world, Entity entity) { }
            }
            [ManagedComponent] public sealed partial class Payload
            {
                public static void Add(IWorld world, Entity entity, Payload? value) { }
                public static void Set(IWorld world, Entity entity, Payload? value) { }
                public static bool TryGet(IWorld world, Entity entity, out Payload? value) { value = null; return false; }
            }
            """;
        var (result, compilation) = GeneratorTestHelper.RunGeneratorsAndCompile(source, rootNamespace: "TestNamespace");
        await AssertCompiles(result, compilation);
    }

    [Test]
    public async Task HandWrittenComponent_StillCompilesAgainstTheExistingWorldApi()
    {
        const string source = """
            using Paradise.ECS;
            public struct Manual : IComponent
            {
                public static ComponentId TypeId => new(17);
                public static System.Guid Guid => System.Guid.Empty;
                public static int Size => sizeof(int);
                public static int Alignment => sizeof(int);
                public int Value;
            }
            public static class Consumer
            {
                public static void Use(IWorld world, Entity entity)
                {
                    world.AddComponent(entity, new Manual { Value = 9 });
                    world.GetComponent<Manual>(entity).Value++;
                    _ = world.HasComponent<Manual>(entity);
                    world.RemoveComponent<Manual>(entity);
                }
            }
            """;
        var (result, compilation) = GeneratorTestHelper.RunGeneratorsAndCompile(source,
            includeTagReference: false, includeManagedReference: false);
        await AssertCompiles(result, compilation);
        await Assert.That(result.GeneratedTrees).IsEmpty();
    }

    [Test]
    public async Task ExplicitGenericWorldImplementations_KeepTheirOriginalMemberDeclarations()
    {
        const string source = """
            using Paradise.ECS;
            public interface LegacyWorld : IWorld<SmallBitSet<ulong>, DefaultConfig>
            {
                Entity IWorld<SmallBitSet<ulong>, DefaultConfig>.Spawn() => default;
                bool IWorld<SmallBitSet<ulong>, DefaultConfig>.Despawn(Entity entity) => false;
                bool IWorld<SmallBitSet<ulong>, DefaultConfig>.IsAlive(Entity entity) => true;
                int IWorld<SmallBitSet<ulong>, DefaultConfig>.EntityCount => 0;
                void IWorld<SmallBitSet<ulong>, DefaultConfig>.AddComponent<T>(Entity entity, T value) { }
                void IWorld<SmallBitSet<ulong>, DefaultConfig>.RemoveComponent<T>(Entity entity) { }
            }
            """;
        var (result, compilation) = GeneratorTestHelper.RunGeneratorsAndCompile(source,
            includeTagReference: false, includeManagedReference: false);
        await AssertCompiles(result, compilation);
    }

    [Test]
    public async Task TagValueAccess_IsRejectedByTheValueInterfaceConstraint()
    {
        const string source = """
            using Paradise.ECS;
            namespace TestNamespace;
            [Tag] public partial struct Active;
            public static class Consumer
            {
                public static Active Invalid(WorldEntity reference) => reference.Get<Active>();
            }
            """;
        var (result, compilation) = GeneratorTestHelper.RunGeneratorsAndCompile(source,
            rootNamespace: "TestNamespace", includeManagedReference: false);
        await Assert.That(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(compilation.GetDiagnostics().Any(d => d.Id == "CS0315")).IsTrue();
        await Assert.That(GeneratedSource(result, "Tag_TestNamespace_Active.g.cs")).DoesNotContain("IEntityComponent<");
    }

    private static async Task AssertCompiles(GeneratorDriverRunResult result, Compilation compilation)
    {
        await Assert.That(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
        var diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error || d.Id.StartsWith("CS86", StringComparison.Ordinal));
        await Assert.That(string.Join("\n", diagnostics)).IsEmpty();
    }

    private static string GeneratedSource(GeneratorDriverRunResult result, string name)
        => result.GeneratedTrees.Single(t => Path.GetFileName(t.FilePath) == name).GetText().ToString();
}
