using Paradise.Assets.Documents;
using Paradise.Assets.Gltf.Test;
using Paradise.Assets.Packing;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

public class SelectivePackTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    [Test]
    public async Task nested_prefabs_pack_runtime_dependencies_and_explicit_dynamic_roots_only()
    {
        using var files = Project("levels/main.prefab", "settings.toml", "prefabs/dynamic.prefab", "audio/init.bnk");
        ProjectVerifierTests.AddAssetWithSidecar(files, "/game/assets/textures/used.png");
        ProjectVerifierTests.AddAssetWithSidecar(files, "/game/assets/textures/unused.png");
        var texture = Reference(files, "textures/used.png");
        ProjectVerifierTests.WriteDocument(files, "/game/assets/materials/used.material",
            $"BaseColorTexture = {{ guid = \"{texture.Guid:D}\", path = \"textures/stale.png\" }}\n");
        var material = Reference(files, "materials/used.material");
        ProjectVerifierTests.WriteDocumentWith(files, "/game/assets/prefabs/child.prefab", new CanonicalTomlTable
        {
            { "Material", AssetReferenceCodec.Write(material) },
            { "RepeatedTexture", AssetReferenceCodec.Write(texture) },
        });
        var main = new PrefabDocument();
        var instance = PrefabObject.WithMeta(Guid.NewGuid(), "child");
        instance.Prefab = Reference(files, "prefabs/child.prefab");
        main.Objects.Add(instance);
        PrefabDocumentSerializer.Save(files, "/game/assets/levels/main.prefab", main);
        ProjectVerifierTests.Mint(files, "/game/assets/levels/main.prefab");
        ProjectVerifierTests.WriteCanonicalDocument(files, "/game/assets/prefabs/dynamic.prefab");
        ProjectVerifierTests.WriteCanonicalDocument(files, "/game/assets/prefabs/unused.prefab");
        ProjectVerifierTests.WriteDocument(files, "/game/assets/settings.toml", "volume = 1\n");
        ProjectVerifierTests.AddAssetWithSidecar(files, "/game/assets/audio/init.bnk");
        ProjectVerifierTests.AddAssetWithSidecar(files, "/game/assets/audio/unused.bnk");

        var result = new BuildRunner(files, s_layout, new BuildRunnerTests.FakeEncoder()).Run("release");
        await Assert.That(result.Errors).IsEmpty();
        using var pack = Pack(files);
        await Assert.That(Paths(pack)).IsEquivalentTo(new[]
        {
            "levels/main.json", "materials/used.material", "textures/used.ktx2",
            "settings.json", "prefabs/dynamic.json", "manifest.json",
        });
        var release = BuildManifest.Load(pack, "/manifest.json");
        await Assert.That(release.Assets.Select(asset => asset.Path)).IsEquivalentTo(new[]
        {
            "levels/main.json", "materials/used.material", "textures/used.ktx2",
            "settings.json", "prefabs/dynamic.json", "audio/init.bnk",
        });
        await Assert.That(release.ByGuid.ContainsKey(Reference(files, "prefabs/child.prefab").Guid.ToString("D"))).IsFalse();
        await Assert.That(release.ByGuid[texture.Guid.ToString("D")].Path).IsEqualTo("textures/used.ktx2");
        await Assert.That(release.FindByGuid(material.Guid.ToString("D"))!.Sha256)
            .IsEqualTo(BuildManifest.Load(files, "/game/build/manifest.json").FindByGuid(material.Guid.ToString("D"))!.Sha256);
        await Assert.That(files.FileExists("/game/build/prefabs/child.json")).IsTrue();
        await Assert.That(files.FileExists("/game/build/textures/unused.ktx2")).IsTrue();
        await Assert.That(files.FileExists("/game/build/audio/init.bnk")).IsTrue();
        await Assert.That(BuildManifest.Load(files, "/game/build/manifest.json").Assets.Count).IsEqualTo(10);
    }

    [Test]
    public async Task runtime_prefab_references_keep_the_prefab_and_follow_its_dependencies()
    {
        using var files = Project("levels/main.prefab");
        ProjectVerifierTests.WriteDocument(files, "/game/assets/settings/spawn.toml", "value = 1\n");
        ProjectVerifierTests.WriteDocumentWith(files, "/game/assets/prefabs/spawn.prefab", new CanonicalTomlTable
        {
            { "Settings", AssetReferenceCodec.Write(Reference(files, "settings/spawn.toml")) },
        });
        ProjectVerifierTests.WriteDocumentWith(files, "/game/assets/levels/main.prefab", new CanonicalTomlTable
        {
            { "Spawn", AssetReferenceCodec.Write(Reference(files, "prefabs/spawn.prefab")) },
        });
        await Assert.That(new BuildRunner(files, s_layout, null).Run("release").Errors).IsEmpty();
        using var pack = Pack(files);
        await Assert.That(Paths(pack)).IsEquivalentTo(new[] { "levels/main.json", "prefabs/spawn.json", "settings/spawn.json", "manifest.json" });
    }

    [Test]
    public async Task skinned_mesh_keeps_its_skeleton_but_not_the_source_or_unused_clips()
    {
        using var files = Project("levels/main.prefab");
        var builder = new GlbTestBuilder();
        var positions = builder.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var joints = builder.AddAccessor(builder.AddBufferView(new byte[12]), GlbTestBuilder.UByte, "VEC4", 3);
        var weights = builder.AddFloatAccessor([1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f], "VEC4");
        var body = builder.AddNode(mesh: builder.AddMesh(GlbTestBuilder.Primitive(positions, joints: joints, weights: weights)), skin: 0, name: "body");
        var joint = builder.AddNode(name: "joint");
        builder.AddSkin([joint]);
        var times = builder.AddFloatAccessor([0f, 1f], "SCALAR");
        var translations = builder.AddFloatAccessor([0f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        builder.AddAnimation("walk", (joint, "translation", times, translations, null));
        builder.AddAnimation("unused", (joint, "translation", times, translations, null));
        builder.SetSceneRoots(body, joint);
        files.WriteAllBytes("/game/assets/models/actor.glb", builder.Build());
        ProjectVerifierTests.Mint(files, "/game/assets/models/actor.glb");
        var source = Reference(files, "models/actor.glb");
        ModelDocument(files, "models/actor.skeleton", new MeshReferenceDocument(source, MeshSlot.Skeleton));
        ModelDocument(files, "models/actor.skinnedmesh", new MeshReferenceDocument(source, MeshSlot.SkinnedMesh, Skeleton: Reference(files, "models/actor.skeleton")));
        ModelDocument(files, "models/walk.anim", new MeshReferenceDocument(source, MeshSlot.Clip, "walk", 0));
        ModelDocument(files, "models/unused.anim", new MeshReferenceDocument(source, MeshSlot.Clip, "unused", 1));
        ProjectVerifierTests.WriteDocumentWith(files, "/game/assets/levels/main.prefab", new CanonicalTomlTable
        {
            { "Mesh", AssetReferenceCodec.Write(Reference(files, "models/actor.skinnedmesh")) },
            { "Animation", AssetReferenceCodec.Write(Reference(files, "models/walk.anim")) },
        });
        await Assert.That(new BuildRunner(files, s_layout, null).Run("release").Errors).IsEmpty();
        using var pack = Pack(files);
        await Assert.That(Paths(pack)).IsEquivalentTo(new[]
        {
            "levels/main.json", "models/actor.skinnedmesh", "models/actor.skeleton", "models/walk.anim", "manifest.json",
        });
        await Assert.That(files.FileExists("/game/build/models/unused.anim")).IsTrue();
        Profile(files, "models/actor.glb");
        var rejected = new BuildRunner(files, s_layout, null).Run("release");
        await Assert.That(rejected.Succeeded).IsFalse();
        await Assert.That(string.Join("\n", rejected.Errors)).Contains("no cooked runtime output");
    }

    [Test]
    public async Task runtime_edges_survive_persisted_reuse_and_update_on_warm_edits_and_removals()
    {
        using var files = Project("root.link");
        var importer = new LinkImporter();
        IAssetImporter[] importers = [.. AssetImporters.All, importer];
        Link(files, importers, "root.link", "child.link");
        Link(files, importers, "child.link", "leaf.link");
        Link(files, importers, "leaf.link", "root.link"); // A runtime cycle terminates and deduplicates.
        Link(files, importers, "unused.link", "");
        var index = AssetIndex.Scan(files, s_layout.Assets);
        var first = new BuildRunner(files, s_layout, null, importers: importers);
        await Assert.That(first.Run("release").Errors).IsEmpty();
        var original = files.ReadAllBytes("/game/build/assets.pak");
        var runner = new BuildRunner(files, s_layout, null, importers: importers);
        await Assert.That(runner.Run("release", sources: index).Errors).IsEmpty();
        await Assert.That(importer.Imports).IsEqualTo(4);
        await Assert.That(files.ReadAllBytes("/game/build/assets.pak").SequenceEqual(original)).IsTrue();
        using (var pack = Pack(files))
            await Assert.That(Paths(pack)).IsEquivalentTo(new[] { "root.link", "child.link", "leaf.link", "manifest.json" });

        var publishedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        files.SetLastWriteTime("/game/build/assets.pak", publishedAt);
        files.WriteAllText("/game/assets/unused.link", "root.link");
        index.Refresh(files, "/game/assets/unused.link");
        await Assert.That(runner.Run("release", sources: index, changedPaths: new HashSet<UPath> { "/game/assets/unused.link" }).Errors).IsEmpty();
        await Assert.That(files.GetLastWriteTime("/game/build/assets.pak")).IsEqualTo(publishedAt);
        await Assert.That(files.ReadAllBytes("/game/build/assets.pak").SequenceEqual(original)).IsTrue();

        files.WriteAllText("/game/assets/child.link", "unused.link");
        index.Refresh(files, "/game/assets/child.link");
        await Assert.That(runner.Run("release", sources: index, changedPaths: new HashSet<UPath> { "/game/assets/child.link" }).Errors).IsEmpty();
        await Assert.That(importer.Imports).IsEqualTo(6);
        using (var pack = Pack(files))
            await Assert.That(Paths(pack)).IsEquivalentTo(new[] { "root.link", "child.link", "unused.link", "manifest.json" });

        var beforeFailure = files.ReadAllBytes("/game/build/assets.pak");
        files.DeleteFile("/game/assets/unused.link");
        files.DeleteFile("/game/assets/unused.link.meta");
        index.Refresh(files, "/game/assets/unused.link");
        var failed = runner.Run("release", sources: index, changedPaths: new HashSet<UPath> { "/game/assets/unused.link" });
        await Assert.That(failed.Succeeded).IsFalse();
        await Assert.That(string.Join("\n", failed.Errors)).Contains("'child.link' requires runtime asset 'unused.link'");
        await Assert.That(files.ReadAllBytes("/game/build/assets.pak").SequenceEqual(beforeFailure)).IsTrue();
        await Assert.That(files.FileExists("/game/build/manifest.json")).IsFalse();
        Link(files, importers, "unused.link", "");
        index.Refresh(files, "/game/assets/unused.link");
        await Assert.That(runner.Run("release", sources: index, changedPaths: new HashSet<UPath>()).Errors).IsEmpty();
    }

    [Test]
    public async Task guid_roots_survive_renames_and_profile_changes_reselect_the_pack()
    {
        using var files = Project();
        ProjectVerifierTests.WriteCanonicalDocument(files, "/game/assets/levels/main.prefab");
        ProjectVerifierTests.WriteCanonicalDocument(files, "/game/assets/levels/other.prefab");
        var identity = Reference(files, "levels/main.prefab").Guid.ToString("D");
        Profile(files, identity, identity); // Duplicate roots are harmless.
        var runner = new BuildRunner(files, s_layout, null);
        await Assert.That(runner.Run("release").Errors).IsEmpty();
        files.MoveFile("/game/assets/levels/main.prefab", "/game/assets/levels/renamed.prefab");
        files.MoveFile("/game/assets/levels/main.prefab.meta", "/game/assets/levels/renamed.prefab.meta");
        var index = AssetIndex.Scan(files, s_layout.Assets);
        await Assert.That(runner.Run("release", sources: index).Errors).IsEmpty();
        using (var pack = Pack(files))
            await Assert.That(Paths(pack)).IsEquivalentTo(new[] { "levels/renamed.json", "manifest.json" });
        Profile(files, "levels/other.prefab");
        await Assert.That(runner.Run("release", sources: index, changedPaths: new HashSet<UPath>()).Errors).IsEmpty();
        using (var pack = Pack(files))
            await Assert.That(Paths(pack)).IsEquivalentTo(new[] { "levels/other.json", "manifest.json" });
        files.WriteAllText(s_layout.Manifest, "name = \"selected\"\nschema_version = 1\n[build.profiles.release]\npack = true\n");
        await Assert.That(runner.Run("release", sources: index, changedPaths: new HashSet<UPath>()).Errors).IsEmpty();
        using (var pack = Pack(files))
            await Assert.That(Paths(pack)).IsEquivalentTo(new[] { "levels/renamed.toml", "levels/other.toml", "manifest.json" });
        await Assert.That(runner.Run().Errors).IsEmpty();
        await Assert.That(files.FileExists("/game/build/assets.pak")).IsFalse();
    }

    [Test]
    public async Task an_explicitly_empty_selection_never_falls_back_to_every_asset()
    {
        using var files = Project();
        ProjectVerifierTests.WriteCanonicalDocument(files, "/game/assets/levels/main.prefab");
        await Assert.That(new BuildRunner(files, s_layout, null).Run("release").Errors).IsEmpty();
        using var pack = Pack(files);
        await Assert.That(Paths(pack)).IsEquivalentTo(new[] { "manifest.json" });
        await Assert.That(BuildManifest.Load(pack, "/manifest.json").ByGuid).IsEmpty();
        await Assert.That(BuildManifest.Load(files, "/game/build/manifest.json").Assets.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("missing.link")]
    [Arguments("Root.link")]
    [Arguments("11111111-2222-4333-8444-555555555555")]
    public async Task unresolved_roots_stop_before_cooking_and_preserve_the_completed_tree(string root)
    {
        using var files = Project("root.link");
        var importer = new LinkImporter();
        IAssetImporter[] importers = [.. AssetImporters.All, importer];
        Link(files, importers, "root.link", "");
        var runner = new BuildRunner(files, s_layout, null, importers: importers);
        await Assert.That(runner.Run("release").Errors).IsEmpty();
        var completed = files.EnumerateFiles("/game/build", "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => files.ReadAllBytes(path));

        files.WriteAllText("/game/assets/root.link", "root.link");
        Profile(files, root);
        var index = AssetIndex.Scan(files, s_layout.Assets);
        var stages = new List<BuildStage>();
        var failed = runner.Run("release", sources: index, changedPaths: new HashSet<UPath>(),
            progress: progress => stages.Add(progress.Stage));
        await Assert.That(failed.Succeeded).IsFalse();
        await Assert.That(failed.Errors.Single()).StartsWith("pack_roots:");
        await Assert.That(failed.Errors.Single()).Contains(root);
        await Assert.That(stages).IsEmpty();
        await Assert.That(importer.Imports).IsEqualTo(1);
        foreach (var (path, bytes) in completed)
            await Assert.That(files.ReadAllBytes(path).SequenceEqual(bytes)).IsTrue();

        Profile(files, "root.link");
        await Assert.That(runner.Run("release", sources: index, changedPaths: new HashSet<UPath>()).Errors).IsEmpty();
        await Assert.That(importer.Imports).IsEqualTo(2);
        using var pack = Pack(files);
        await Assert.That(pack.ReadAllText("/root.link")).IsEqualTo("root.link");
    }

    [Test]
    public async Task a_runtime_dependency_with_wrong_case_names_the_case_exact_contract()
    {
        using var files = Project("root.link");
        IAssetImporter[] importers = [.. AssetImporters.All, new LinkImporter()];
        Link(files, importers, "root.link", "Child.link");
        Link(files, importers, "child.link", "");
        var result = new BuildRunner(files, s_layout, null, importers: importers).Run("release");
        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors.Single()).Contains("requires runtime asset 'Child.link'");
        await Assert.That(result.Errors.Single()).Contains("case-exact");
        await Assert.That(files.FileExists("/game/build/child.link")).IsTrue();
        await Assert.That(files.FileExists("/game/build/assets.pak")).IsFalse();
        await Assert.That(files.FileExists("/game/build/manifest.json")).IsFalse();
    }

    [Test]
    public async Task a_path_only_payload_stays_data_and_does_not_create_a_runtime_reference()
    {
        using var files = Project("levels/main.prefab");
        ProjectVerifierTests.WriteDocumentWith(files, "/game/assets/levels/main.prefab", new CanonicalTomlTable
        {
            { "Payload", new CanonicalInlineTable { { "path", "missing.toml" } } },
        });
        await Assert.That(new BuildRunner(files, s_layout, null).Run("release").Errors).IsEmpty();
        using var pack = Pack(files);
        await Assert.That(Paths(pack)).IsEquivalentTo(new[] { "levels/main.json", "manifest.json" });
        await Assert.That(pack.ReadAllText("/levels/main.json")).Contains("\"path\": \"missing.toml\"");
    }

    [Test]
    public async Task a_reference_with_an_invalid_guid_fails_verification_before_cooking()
    {
        using var files = Project("levels/main.prefab");
        ProjectVerifierTests.WriteDocumentWith(files, "/game/assets/levels/main.prefab", new CanonicalTomlTable
        {
            { "Asset", new CanonicalInlineTable { { "guid", "invalid" }, { "path", "missing.toml" } } },
        });
        var stages = new List<BuildStage>();
        var result = new BuildRunner(files, s_layout, null).Run("release", progress: progress => stages.Add(progress.Stage));
        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(string.Join("\n", result.Errors)).Contains("invalid");
        await Assert.That(stages.Contains(BuildStage.Assets)).IsFalse();
        await Assert.That(files.DirectoryExists("/game/build")).IsFalse();
    }

    [Test]
    [Arguments("missing.prefab")]
    [Arguments("levels/MAIN.prefab")]
    [Arguments("11111111-2222-4333-8444-555555555555")]
    [Arguments("project.toml")]
    public async Task missing_or_build_only_roots_fail_without_publishing_a_manifest(string root)
    {
        using var files = Project(root);
        ProjectVerifierTests.WriteCanonicalDocument(files, "/game/assets/levels/main.prefab");
        var result = new BuildRunner(files, s_layout, null).Run("release");
        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(string.Join("\n", result.Errors)).Contains("pack_roots");
        await Assert.That(string.Join("\n", result.Errors)).Contains(root);
        await Assert.That(files.FileExists("/game/build/manifest.json")).IsFalse();
        await Assert.That(files.FileExists("/game/build/assets.pak")).IsFalse();
    }

    private static MemoryFileSystem Project(params string[] roots)
    {
        var files = ProjectVerifierTests.CreateProject();
        Profile(files, roots);
        return files;
    }

    private static void Profile(MemoryFileSystem files, params string[] roots)
        => files.WriteAllText(s_layout.Manifest,
            "name = \"selected\"\nschema_version = 1\n[build.profiles.release]\npack = true\ndocument_format = \"json\"\npack_roots = ["
            + string.Join(", ", roots.Select(root => $"\"{root}\"")) + "]\n");

    private static AssetReference Reference(MemoryFileSystem files, string path)
        => new(SidecarMeta.Load(files, s_layout.Assets / (path + ".meta")).Guid, path);

    private static AssetPackFileSystem Pack(MemoryFileSystem files) => new(files.ReadAllBytes("/game/build/assets.pak"));

    private static string[] Paths(IFileSystem pack)
        => pack.EnumerateFiles("/", "*", SearchOption.AllDirectories).Select(path => path.FullName[1..]).ToArray();

    private static void ModelDocument(MemoryFileSystem files, string path, MeshReferenceDocument document)
    {
        files.WriteAllBytes(s_layout.Assets / path, document.WriteBytes());
        ProjectVerifierTests.Mint(files, s_layout.Assets / path);
    }

    private static void Link(MemoryFileSystem files, IReadOnlyList<IAssetImporter> importers, string path, string dependency)
    {
        files.WriteAllText(s_layout.Assets / path, dependency);
        ProjectVerifierTests.Mint(files, s_layout.Assets / path, importers: importers);
    }

    private sealed class LinkImporter : IAssetImporter
    {
        public string Name => "link";
        public bool RecordsIdentity => true;
        public int Imports { get; private set; }
        public bool Claims(ImportCandidate candidate) => candidate.HasExtension(".link");
        public bool Import(ImportContext context, List<string> errors)
        {
            Imports++;
            var dependency = context.FileSystem.ReadAllText(context.Asset);
            if (dependency.Length != 0) context.RequireRuntimeAsset(dependency);
            context.Output.WriteAllText("/" + context.Source, dependency);
            return true;
        }
    }
}
