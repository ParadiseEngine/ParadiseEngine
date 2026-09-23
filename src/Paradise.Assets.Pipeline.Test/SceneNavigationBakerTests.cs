using System.Security.Cryptography;
using System.Text.Json;

using Paradise.Assets.Documents;
using Paradise.Assets.Gltf.Test;
using Paradise.Assets.Project;
using Paradise.Authoring;

namespace Paradise.Assets.Pipeline.Test;

public class SceneNavigationBakerTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");
    private static readonly Guid s_navigation = Guid.Parse("51000000-0000-4000-8000-000000000001");
    private static readonly Guid s_mesh = Guid.Parse("51000000-0000-4000-8000-000000000002");
    private static readonly Guid s_participation = Guid.Parse("51000000-0000-4000-8000-000000000003");
    private static readonly Guid s_root = Guid.Parse("51000000-0000-4000-8000-000000000004");
    private static readonly Guid s_rigidbody = Guid.Parse("51000000-0000-4000-8000-000000000005");
    private const string Document = "/game/assets/levels/arena.prefab";
    private const string Output = "/game/assets/levels/arena.navmesh";

    [Test]
    public async Task bake_derives_path_updates_only_component_value_and_reloads_preview()
    {
        using var fs = CreateProject();
        var result = SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation);

        await Assert.That(result.DocumentChanged).IsTrue();
        await Assert.That(result.RelativePath).IsEqualTo("levels/arena.navmesh");
        await Assert.That(result.Output.FullName).IsEqualTo(Output);
        await Assert.That(result.Preview.Indices.Length).IsGreaterThan(0);
        var component = PrefabDocumentSerializer.Load(fs, Document).Root.Component(s_navigation)!;
        await Assert.That(component.Data.Value("NavMeshFile")).IsEqualTo("levels/arena.navmesh");
        await Assert.That(((CanonicalTomlTable)component.Data.Value("Future")!).Value("Answer")).IsEqualTo(42L);
        await Assert.That(component.Data.Value("Label")).IsEqualTo("Keep me");
        var preview = SceneNavigationBaker.Preview(fs, s_layout, Document, Schema(), s_navigation);
        await Assert.That(preview.Indices.SequenceEqual(result.Preview.Indices)).IsTrue();
        await Assert.That(result.DocumentHash).IsEqualTo(Hash(fs.ReadAllBytes(Document)));
        var sidecar = SidecarMeta.Load(fs, Output + ".meta");
        await Assert.That(sidecar.Importer).IsEqualTo("navmesh");
        await Assert.That(sidecar.Guid).IsNotEqualTo(Guid.Empty);
    }

    [Test]
    public async Task rebake_keeps_the_sidecar_identity_and_a_recorded_importer_is_never_overwritten()
    {
        using var fs = CreateProject();
        await Assert.That(fs.FileExists(Output + ".meta")).IsFalse();

        SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation);
        var minted = SidecarMeta.Load(fs, Output + ".meta");

        SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation);
        var kept = SidecarMeta.Load(fs, Output + ".meta");
        await Assert.That(kept.Guid).IsEqualTo(minted.Guid);
        await Assert.That(kept.Importer).IsEqualTo("navmesh");

        var authorChosen = new SidecarMeta(minted.Guid) { Importer = "author-override" };
        authorChosen.Save(fs, Output + ".meta");
        SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation);
        var honored = SidecarMeta.Load(fs, Output + ".meta");
        await Assert.That(honored.Importer).IsEqualTo("author-override");
        await Assert.That(honored.Guid).IsEqualTo(minted.Guid);
    }

    [Test]
    public async Task failed_bake_leaves_no_sidecar_behind()
    {
        using var memory = CreateProject();
        memory.WriteAllBytes(Output, [8, 9]);
        using var fs = new RefuseDocumentPublish(memory);

        await Assert.That(() => SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation))
            .Throws<IOException>();
        await Assert.That(memory.FileExists(Output + ".meta")).IsFalse();
        await Assert.That(memory.ReadAllBytes(Output).SequenceEqual(new byte[] { 8, 9 })).IsTrue();
    }

    [Test]
    public async Task normalize_without_baking_preserves_binary_and_is_byte_stable_afterward()
    {
        using var fs = CreateProject();
        fs.WriteAllBytes(Output, [3, 4, 5]);
        var first = SceneNavigationBaker.Normalize(fs, s_layout, Document, Schema(), s_navigation);
        var bytes = fs.ReadAllBytes(Document);
        var second = SceneNavigationBaker.Normalize(fs, s_layout, Document, Schema(), s_navigation);

        await Assert.That(first.DocumentChanged).IsTrue();
        await Assert.That(second.DocumentChanged).IsFalse();
        await Assert.That(fs.ReadAllBytes(Document).SequenceEqual(bytes)).IsTrue();
        await Assert.That(fs.ReadAllBytes(Output).SequenceEqual(new byte[] { 3, 4, 5 })).IsTrue();
    }

    [Test]
    public async Task moved_asset_hint_resolves_by_guid()
    {
        using var fs = CreateProject();
        fs.MoveFile("/game/assets/models/floor.mesh", "/game/assets/models/renamed.mesh");
        fs.MoveFile("/game/assets/models/floor.mesh.meta", "/game/assets/models/renamed.mesh.meta");

        var result = SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation);

        await Assert.That(result.Preview.Indices.Length).IsGreaterThan(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task geometry_opt_out_excludes_descendants_including_schema_default(bool explicitValue)
    {
        using var fs = CreateProject();
        var level = PrefabDocumentSerializer.Load(fs, Document);
        var carrier = PrefabObject.WithMeta(Guid.NewGuid(), "Moving car", s_root);
        var data = new CanonicalTomlTable();
        if (explicitValue) data.Add("Geometry", false);
        carrier.Components.Add(new PrefabComponent(s_participation, "Tests.Participation", data));
        level.Objects.Add(carrier);
        var child = PrefabObject.WithMeta(Guid.NewGuid(), "Excluded distant mesh", carrier.Guid);
        child.Components.Add(Transform(10000f, 0f, 0f));
        child.Components.Add(Mesh(level.Objects[1].Component(s_mesh)!.Data.Value("Mesh")!));
        level.Objects.Add(child);
        PrefabDocumentSerializer.Save(fs, Document, level);

        var result = SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation);

        await Assert.That(result.Preview.Vertices.Max(point => point[0])).IsLessThan(20f);
    }

    [Test]
    [Arguments("Dynamic")]
    [Arguments("Kinematic")]
    [Arguments(null)]
    public async Task moving_rigidbody_excludes_the_entire_subtree(string? bodyType)
    {
        using var fs = CreateProject();
        var level = PrefabDocumentSerializer.Load(fs, Document);
        var moving = PrefabObject.WithMeta(Guid.NewGuid(), "Moving rigidbody", s_root);
        var body = new CanonicalTomlTable();
        if (bodyType is not null) body.Add("BodyType", bodyType);
        moving.Components.Add(new PrefabComponent(s_rigidbody, "Tests.Rigidbody", body));
        level.Objects.Add(moving);
        var child = PrefabObject.WithMeta(Guid.NewGuid(), "Moving floor", moving.Guid);
        child.Components.Add(Transform(10000, 0, 0));
        child.Components.Add(Mesh(level.Objects[1].Component(s_mesh)!.Data.Value("Mesh")!));
        level.Objects.Add(child);
        PrefabDocumentSerializer.Save(fs, Document, level);

        var result = SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation);

        await Assert.That(result.Preview.Vertices.Max(point => point[0])).IsLessThan(20f);
    }

    [Test]
    [Arguments("Static")]
    [Arguments("None")]
    public async Task static_and_unclassified_bodies_retain_geometry(string bodyType)
    {
        using var fs = CreateProject();
        var level = PrefabDocumentSerializer.Load(fs, Document);
        level.Objects[1].Components.Add(new PrefabComponent(s_rigidbody, "Tests.Rigidbody",
            new CanonicalTomlTable { { "BodyType", bodyType } }));
        PrefabDocumentSerializer.Save(fs, Document, level);

        var result = SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation);

        await Assert.That(result.Preview.Indices.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task body_schema_default_is_applied_when_published()
    {
        using var fs = CreateProject();
        var level = PrefabDocumentSerializer.Load(fs, Document);
        level.Objects[1].Components.Add(new PrefabComponent(s_rigidbody, "Tests.Rigidbody"));
        PrefabDocumentSerializer.Save(fs, Document, level);
        var schema = Schema();
        schema.Components.Single(component => component.Id == s_rigidbody).Fields[0].Default =
            JsonSerializer.SerializeToElement("Static");

        var result = SceneNavigationBaker.Bake(fs, s_layout, Document, schema, s_navigation);

        await Assert.That(result.Preview.Indices.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task invalid_body_value_fails_the_bake()
    {
        using var fs = CreateProject();
        var level = PrefabDocumentSerializer.Load(fs, Document);
        level.Objects[1].Components.Add(new PrefabComponent(s_rigidbody, "Tests.Rigidbody",
            new CanonicalTomlTable { { "BodyType", "Hovering" } }));
        PrefabDocumentSerializer.Save(fs, Document, level);

        await Assert.That(() => SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation))
            .Throws<InvalidDataException>().WithMessageContaining("Hovering");
    }

    [Test]
    public async Task excluded_geometry_does_not_resolve_meshes_before_later_participation_marker()
    {
        using var fs = CreateProject();
        var level = PrefabDocumentSerializer.Load(fs, Document);
        var excluded = PrefabObject.WithMeta(Guid.NewGuid(), "Excluded object", s_root);
        excluded.Components.Add(Transform(1, 0, 0));
        excluded.Components.Add(Mesh("invalid excluded reference"));
        excluded.Components.Add(new PrefabComponent(s_participation, "Tests.Participation"));
        level.Objects.Add(excluded);
        PrefabDocumentSerializer.Save(fs, Document, level);

        var result = SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation);

        await Assert.That(result.Preview.Indices.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task skinned_mesh_and_its_children_do_not_enter_static_geometry()
    {
        using var fs = CreateProject();
        var index = AssetIndex.Scan(fs, s_layout.Assets);
        var glb = index.Files.Single(path => path.GetExtensionWithDot() == ".glb");
        var source = new AssetReference(SidecarMeta.Load(fs, glb + ".meta").Guid, "models/floor.glb");
        fs.WriteAllBytes("/game/assets/models/actor.skinnedmesh", new MeshReferenceDocument(source, MeshSlot.SkinnedMesh,
            Skeleton: new AssetReference(Guid.NewGuid(), "models/actor.skeleton")).WriteBytes());
        var actorId = ProjectVerifierTests.Mint(fs, "/game/assets/models/actor.skinnedmesh");
        var level = PrefabDocumentSerializer.Load(fs, Document);
        var actor = PrefabObject.WithMeta(Guid.NewGuid(), "Actor", s_root);
        actor.Components.Add(Transform(10000f, 0f, 0f));
        actor.Components.Add(Mesh(AssetReferenceCodec.Write(new AssetReference(actorId, "models/actor.skinnedmesh"))));
        level.Objects.Add(actor);
        var child = PrefabObject.WithMeta(Guid.NewGuid(), "Accessory", actor.Guid);
        child.Components.Add(Mesh(level.Objects[1].Component(s_mesh)!.Data.Value("Mesh")!));
        level.Objects.Add(child);
        PrefabDocumentSerializer.Save(fs, Document, level);

        var result = SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation);

        await Assert.That(result.Preview.Vertices.Max(point => point[0])).IsLessThan(20f);
    }

    [Test]
    public async Task schema_skinned_mesh_kind_excludes_actor_children_without_a_selected_asset()
    {
        using var fs = CreateProject();
        var schema = Schema();
        var skinned = Guid.NewGuid();
        schema.Components.Add(new AuthoredComponentSchema { Id = skinned, Fields =
            [new AuthoredFieldSchema { Name = "Geometry", AuthoredBy = AuthoredBySources.Mesh, AssetKinds = [".skinnedmesh"] }] });
        var level = PrefabDocumentSerializer.Load(fs, Document);
        var actor = PrefabObject.WithMeta(Guid.NewGuid(), "Actor without asset", s_root);
        actor.Components.Add(new PrefabComponent(skinned, "Tests.SkinnedMesh"));
        level.Objects.Add(actor);
        var child = PrefabObject.WithMeta(Guid.NewGuid(), "Accessory", actor.Guid);
        child.Components.Add(Transform(10000, 0, 0));
        child.Components.Add(Mesh(level.Objects[1].Component(s_mesh)!.Data.Value("Mesh")!));
        level.Objects.Add(child);
        PrefabDocumentSerializer.Save(fs, Document, level);

        var result = SceneNavigationBaker.Bake(fs, s_layout, Document, schema, s_navigation);

        await Assert.That(result.Preview.Vertices.Max(point => point[0])).IsLessThan(20f);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task invalid_mesh_reference_preserves_previous_binary_and_canonical_document(bool malformed)
    {
        using var fs = CreateProject();
        var level = PrefabDocumentSerializer.Load(fs, Document);
        var mesh = level.Objects[1];
        mesh.Components.Remove(mesh.Component(s_mesh)!);
        mesh.Components.Add(Mesh(malformed ? "models/not-a-reference.mesh" :
            AssetReferenceCodec.Write(new AssetReference(Guid.NewGuid(), "models/missing.mesh"))));
        PrefabDocumentSerializer.Save(fs, Document, level);
        var before = fs.ReadAllBytes(Document);
        fs.WriteAllBytes(Output, [8, 9]);

        await Assert.That(() => SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation))
            .Throws<InvalidDataException>();
        await Assert.That(fs.ReadAllBytes(Document).SequenceEqual(before)).IsTrue();
        await Assert.That(fs.ReadAllBytes(Output).SequenceEqual(new byte[] { 8, 9 })).IsTrue();
    }

    [Test]
    public async Task invalid_prefab_dependency_refuses_partial_level_bake()
    {
        using var fs = CreateProject();
        var level = PrefabDocumentSerializer.Load(fs, Document);
        var missing = PrefabObject.WithMeta(Guid.NewGuid(), "Missing instance", s_root);
        missing.Prefab = new AssetReference(Guid.NewGuid(), "prefabs/missing.prefab");
        level.Objects.Add(missing);
        PrefabDocumentSerializer.Save(fs, Document, level);
        fs.WriteAllBytes(Output, [8, 9]);

        await Assert.That(() => SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation))
            .Throws<InvalidDataException>().WithMessageContaining("missing.prefab");
        await Assert.That(fs.ReadAllBytes(Output).SequenceEqual(new byte[] { 8, 9 })).IsTrue();
    }

    [Test]
    public async Task stale_request_is_refused_before_any_output_changes()
    {
        using var fs = CreateProject();
        fs.WriteAllBytes(Output, [8, 9]);
        var before = fs.ReadAllBytes(Document);

        await Assert.That(() => SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation,
            expectedDocumentHash: Hash([1, 2])))
            .Throws<InvalidDataException>().WithMessageContaining("changed");
        await Assert.That(fs.ReadAllBytes(Document).SequenceEqual(before)).IsTrue();
        await Assert.That(fs.ReadAllBytes(Output).SequenceEqual(new byte[] { 8, 9 })).IsTrue();
    }

    [Test]
    public async Task edit_while_baking_preserves_new_document_and_previous_binary()
    {
        using var memory = CreateProject();
        memory.WriteAllBytes(Output, [8, 9]);
        var changed = memory.ReadAllText(Document).Replace("Keep me", "New edit", StringComparison.Ordinal);
        using var fs = new EditOnStage(memory, () => memory.WriteAllText(Document, changed));

        await Assert.That(() => SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation))
            .Throws<InvalidDataException>().WithMessageContaining("changed during");
        await Assert.That(memory.ReadAllText(Document)).IsEqualTo(changed);
        await Assert.That(memory.ReadAllBytes(Output).SequenceEqual(new byte[] { 8, 9 })).IsTrue();
        await Assert.That(memory.EnumerateFiles("/game/assets/levels", "*.tmp", SearchOption.TopDirectoryOnly).Count())
            .IsEqualTo(0);
    }

    [Test]
    public async Task failed_canonical_write_restores_previous_navigation_binary()
    {
        using var memory = CreateProject();
        memory.WriteAllBytes(Output, [8, 9]);
        var original = memory.ReadAllBytes(Document);
        // A sidecar from before importers were recorded: the bake stages an importer refresh, and
        // the rollback must restore this file rather than leave the refreshed one or delete it.
        var prior = new SidecarMeta(Guid.NewGuid());
        prior.Save(memory, Output + ".meta");
        using var fs = new RefuseDocumentPublish(memory);

        await Assert.That(() => SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation))
            .Throws<IOException>();
        await Assert.That(memory.ReadAllBytes(Document).SequenceEqual(original)).IsTrue();
        await Assert.That(memory.ReadAllBytes(Output).SequenceEqual(new byte[] { 8, 9 })).IsTrue();
        var restored = SidecarMeta.Load(memory, Output + ".meta");
        await Assert.That(restored.Guid).IsEqualTo(prior.Guid);
        await Assert.That(restored.Importer).IsNull();
    }

    [Test]
    public async Task failed_binary_rollback_keeps_the_previous_binary_and_reports_its_recovery_path()
    {
        using var memory = CreateProject();
        memory.WriteAllBytes(Output, [8, 9]);
        var original = memory.ReadAllBytes(Document);
        using var fs = new RefuseDocumentAndRollback(memory);

        var failure = await Assert.That(() => SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation))
            .Throws<IOException>();

        var backups = memory.EnumerateFiles("/game/assets/levels", "*.tmp", SearchOption.TopDirectoryOnly).ToArray();
        await Assert.That(backups.Length).IsEqualTo(1);
        await Assert.That(memory.ReadAllBytes(backups[0]).SequenceEqual(new byte[] { 8, 9 })).IsTrue();
        await Assert.That(failure!.Message).Contains(backups[0].FullName);
        await Assert.That(memory.ReadAllBytes(Document).SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task mirrored_level_geometry_retains_walkable_winding()
    {
        using var fs = CreateProject();
        var level = PrefabDocumentSerializer.Load(fs, Document);
        var floor = level.Objects[1];
        floor.Components.Remove(floor.Component(WellKnownComponents.TransformId)!);
        floor.Components.Add(new PrefabComponent(WellKnownComponents.TransformId, WellKnownComponents.TransformType,
            new CanonicalTomlTable { { WellKnownComponents.Scale, new object[] { -1d, 1d, 1d } } }));
        PrefabDocumentSerializer.Save(fs, Document, level);

        var result = SceneNavigationBaker.Bake(fs, s_layout, Document, Schema(), s_navigation);

        await Assert.That(result.Preview.Indices.Length).IsGreaterThan(0);
    }

    [Test]
    [Arguments("/game/assets/levels/arena.navmesh")]
    [Arguments("/other/arena.prefab")]
    public async Task derived_output_cannot_escape_assets_or_overwrite_input(string document)
    {
        using var fs = CreateProject();
        await Assert.That(() => SceneNavigationBaker.Normalize(fs, s_layout, document, Schema(), s_navigation))
            .Throws<ArgumentException>();
    }

    private static MemoryFileSystem CreateProject()
    {
        var fs = ProjectVerifierTests.CreateProject();
        var builder = new GlbTestBuilder();
        var position = builder.AddFloatAccessor([-10, 0, -10, -10, 0, 10, 10, 0, 10, 10, 0, -10], "VEC3");
        var indices = builder.AddIndexAccessor([0, 1, 2, 0, 2, 3]);
        var node = builder.AddNode(mesh: builder.AddMesh(GlbTestBuilder.Primitive(position, indices: indices)));
        builder.SetSceneRoots(node);
        fs.WriteAllBytes("/game/assets/models/floor.glb", builder.Build());
        var glb = ProjectVerifierTests.Mint(fs, "/game/assets/models/floor.glb");
        fs.WriteAllBytes("/game/assets/models/floor.mesh",
            new MeshReferenceDocument(new AssetReference(glb, "models/floor.glb"), MeshSlot.Mesh).WriteBytes());
        var mesh = ProjectVerifierTests.Mint(fs, "/game/assets/models/floor.mesh");
        var level = new PrefabDocument();
        var root = PrefabObject.WithMeta(s_root, "Arena");
        root.Components.Add(new PrefabComponent(s_navigation, "Tests.Navigation", new CanonicalTomlTable
        {
            { "NavMeshFile", "levels/old.navmesh" },
            { "Label", "Keep me" },
            { "Future", new CanonicalInlineTable { { "Answer", 42L } } },
        }));
        level.Objects.Add(root);
        var floor = PrefabObject.WithMeta(Guid.NewGuid(), "Floor", s_root);
        floor.Components.Add(Transform(0, 0, 0));
        floor.Components.Add(Mesh(AssetReferenceCodec.Write(new AssetReference(mesh, "models/floor.mesh"))));
        level.Objects.Add(floor);
        PrefabDocumentSerializer.Save(fs, Document, level);
        ProjectVerifierTests.Mint(fs, Document);
        return fs;
    }

    private static AuthoringSchemaDocument Schema() => new()
    {
        Components =
        [
            new AuthoredComponentSchema { Id = s_navigation, Fields =
            [new AuthoredFieldSchema { Name = "NavMeshFile", Type = AuthoredFieldTypes.String, AuthoredBy = "navmesh" }] },
            new AuthoredComponentSchema { Id = s_mesh, Fields =
            [new AuthoredFieldSchema { Name = "Mesh", Type = AuthoredFieldTypes.String, AuthoredBy = AuthoredBySources.Mesh }] },
            new AuthoredComponentSchema { Id = s_participation, Fields =
            [new AuthoredFieldSchema { Name = "Geometry", Type = AuthoredFieldTypes.Bool, AuthoredBy = "navmesh-geometry",
                Default = JsonSerializer.SerializeToElement(false) }] },
            new AuthoredComponentSchema { Id = s_rigidbody, Fields =
            [new AuthoredFieldSchema { Name = "BodyType", Type = AuthoredFieldTypes.Enum, AuthoredBy = "navmesh-body",
                Values = ["None", "Static", "Kinematic", "Dynamic"] }] },
        ],
    };

    private static PrefabComponent Mesh(object reference) =>
        new(s_mesh, "Tests.Mesh", new CanonicalTomlTable { { "Mesh", reference } });

    private static PrefabComponent Transform(float x, float y, float z) => new(WellKnownComponents.TransformId,
        WellKnownComponents.TransformType, new CanonicalTomlTable
        { { WellKnownComponents.Position, new object[] { (double)x, (double)y, (double)z } } });

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class EditOnStage(IFileSystem fallback, Action edit) : Zio.FileSystems.ComposeFileSystem(fallback, owned: false)
    {
        private bool _edited;
        protected override UPath ConvertPathToDelegate(UPath path) => path;
        protected override UPath ConvertPathFromDelegate(UPath path) => path;

        protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
        {
            if (!_edited && mode == FileMode.CreateNew)
            {
                _edited = true;
                edit();
            }
            return base.OpenFileImpl(path, mode, access, share);
        }
    }

    private sealed class RefuseDocumentPublish(IFileSystem fallback) : ComposeFileSystem(fallback, owned: false)
    {
        protected override UPath ConvertPathToDelegate(UPath path) => path;
        protected override UPath ConvertPathFromDelegate(UPath path) => path;
        protected override void ReplaceFileImpl(UPath source, UPath destination, UPath backup, bool ignoreMetadataErrors)
        {
            if (destination.FullName == Document) throw new IOException("Document is locked.");
            base.ReplaceFileImpl(source, destination, backup, ignoreMetadataErrors);
        }
    }

    private sealed class RefuseDocumentAndRollback(IFileSystem fallback) : ComposeFileSystem(fallback, owned: false)
    {
        private bool _published;
        protected override UPath ConvertPathToDelegate(UPath path) => path;
        protected override UPath ConvertPathFromDelegate(UPath path) => path;

        protected override void ReplaceFileImpl(UPath source, UPath destination, UPath backup, bool ignoreMetadataErrors)
        {
            if (destination.FullName == Document) throw new IOException("Document is locked.");
            if (destination.FullName == Output)
            {
                if (_published) throw new IOException("Navigation binary is now locked.");
                _published = true;
            }
            base.ReplaceFileImpl(source, destination, backup, ignoreMetadataErrors);
        }
    }
}
