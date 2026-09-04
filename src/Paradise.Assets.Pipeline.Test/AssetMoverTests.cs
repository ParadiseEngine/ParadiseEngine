using Paradise.Assets.Documents;
using Paradise.Assets.Project;

namespace Paradise.Assets.Pipeline.Test;

public class AssetMoverTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");
    private static readonly Guid s_crate = new("11111111-2222-4333-8444-555555555555");
    private static readonly Guid s_root = new("3f2a1b4c-5d6e-4f70-8192-a3b4c5d6e7f8");

    [Test]
    public async Task a_file_moves_with_its_sidecar_and_every_reference_follows()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", [1]);
        ProjectVerifierTests.Mint(fileSystem, "/game/assets/models/crate.glb", s_crate);
        WriteLevel(fileSystem, "/game/assets/levels/district.prefab", "models/crate.glb");

        var result = AssetMover.Move(fileSystem, s_layout, "/game/assets/models/crate.glb", "/game/assets/props/box/crate.glb");

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(fileSystem.FileExists("/game/assets/props/box/crate.glb")).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb")).IsFalse();
        // Identity travelled untouched: the guid every reference names is still this asset's.
        await Assert.That(SidecarMeta.Load(fileSystem, "/game/assets/props/box/crate.glb.meta").Guid).IsEqualTo(s_crate);
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb.meta")).IsFalse();

        await Assert.That(result.Rewritten).IsEquivalentTo(new[] { "levels/district.prefab" });
        var document = PrefabDocumentSerializer.Load(fileSystem, "/game/assets/levels/district.prefab");
        var mesh = (CanonicalInlineTable)document.Objects[0].Components[1].Data.Value("Mesh")!;
        await Assert.That(mesh.Value("path")).IsEqualTo("props/box/crate.glb");
        await Assert.That(mesh.Value("guid")).IsEqualTo(DocumentGuid.Format(s_crate));
        // The tree the move leaves behind is one verify accepts.
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout).Where(f => f.Severity == VerifySeverity.Error)).IsEmpty();
    }

    [Test]
    public async Task a_directory_moves_as_a_whole_and_references_into_it_follow()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", [1]);
        ProjectVerifierTests.Mint(fileSystem, "/game/assets/models/crate.glb", s_crate);
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/sub/lid.glb");
        WriteLevel(fileSystem, "/game/assets/levels/district.prefab", "models/crate.glb");
        // A document inside the moved directory keeps working: references are root-relative.
        WriteLevel(fileSystem, "/game/assets/models/sub/inner.prefab", "models/crate.glb");

        var result = AssetMover.Move(fileSystem, s_layout, "/game/assets/models", "/game/assets/props");

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Moved).Contains("props/crate.glb");
        await Assert.That(result.Moved).Contains("props/sub/lid.glb");
        await Assert.That(fileSystem.FileExists("/game/assets/props/sub/lid.glb.meta")).IsTrue();
        await Assert.That(result.Rewritten).Contains("levels/district.prefab");
        await Assert.That(result.Rewritten).Contains("props/sub/inner.prefab");
        await Assert.That(fileSystem.ReadAllText("/game/assets/props/sub/inner.prefab")).Contains("props/crate.glb");
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout).Where(f => f.Severity == VerifySeverity.Error)).IsEmpty();
    }

    [Test]
    public async Task moving_into_an_existing_directory_keeps_the_name_like_mv_does()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        fileSystem.CreateDirectory("/game/assets/props");

        var result = AssetMover.Move(fileSystem, s_layout, "/game/assets/models/crate.glb", "/game/assets/props");

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Moved).IsEquivalentTo(new[] { "props/crate.glb" });
        await Assert.That(fileSystem.FileExists("/game/assets/props/crate.glb.meta")).IsTrue();
    }

    [Test]
    public async Task a_prefab_reference_and_a_reference_inside_a_list_follow_too()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var boxGuid = new Guid("4c296cda-6844-574c-8f56-8ab5e04bbd20");
        ProjectVerifierTests.WriteCanonicalDocument(fileSystem, "/game/assets/prefabs/box.prefab");
        var box = PrefabDocumentSerializer.Load(fileSystem, "/game/assets/prefabs/box.prefab");
        ProjectVerifierTests.Mint(fileSystem, "/game/assets/prefabs/box.prefab", boxGuid);
        fileSystem.CreateDirectory("/game/assets/materials");
        fileSystem.WriteAllBytes("/game/assets/materials/grass.toml", [1]);
        ProjectVerifierTests.Mint(fileSystem, "/game/assets/materials/grass.toml", s_crate);

        var level = new PrefabDocument();
        var root = PrefabObject.WithMeta(s_root, "level");
        level.Objects.Add(root);
        var instance = new PrefabObject { Prefab = new Paradise.Authoring.AssetReference(boxGuid, "prefabs/box.prefab") };
        instance.Components.Add(new PrefabComponent(WellKnownComponents.MetaId, WellKnownComponents.MetaType,
            new CanonicalTomlTable { { WellKnownComponents.Guid, DocumentGuid.Format(Guid.NewGuid()) }, { WellKnownComponents.Parent, DocumentGuid.Format(s_root) } }));
        instance.Components.Add(new PrefabComponent(new Guid("bdc4fc87-d7b4-41f1-bc90-fc827005adfc"), "Materials",
            new CanonicalTomlTable
            {
                { "Slots", new List<object> { AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(s_crate, "materials/grass.toml")), new CanonicalInlineTable() } },
            }));
        level.Objects.Add(instance);
        PrefabDocumentSerializer.Save(fileSystem, "/game/assets/levels/district.prefab", level);
        ProjectVerifierTests.MintDocumentSidecar(fileSystem, "/game/assets/levels/district.prefab");

        var first = AssetMover.Move(fileSystem, s_layout, "/game/assets/prefabs", "/game/assets/props");
        var second = AssetMover.Move(fileSystem, s_layout, "/game/assets/materials/grass.toml", "/game/assets/materials/ground.toml");

        await Assert.That(first.Errors).IsEmpty();
        await Assert.That(second.Errors).IsEmpty();
        var text = fileSystem.ReadAllText("/game/assets/levels/district.prefab");
        await Assert.That(text).Contains("path = \"props/box.prefab\"");
        await Assert.That(text).Contains("path = \"materials/ground.toml\" }, {}]");
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout).Where(f => f.Severity == VerifySeverity.Error)).IsEmpty();
    }

    [Test]
    public async Task an_unrelated_document_is_left_byte_for_byte_alone()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        // Not canonical on purpose: mv must not reformat what it did not need to touch.
        ProjectVerifierTests.WriteDocument(fileSystem, "/game/assets/levels/other.prefab",
            "schema_version = 1\n\n[[objects]]\n\n[[objects.components]]\nid = \"0f1d4b3a-8c27-4a55-9b6e-2f7c1d40a913\"\ntype = \"meta\"\nGuid = \"3f2a1b4c-5d6e-4f70-8192-a3b4c5d6e7f8\"\n# a comment\n");
        var before = fileSystem.ReadAllText("/game/assets/levels/other.prefab");

        var result = AssetMover.Move(fileSystem, s_layout, "/game/assets/models/crate.glb", "/game/assets/models/box.glb");

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Rewritten).IsEmpty();
        await Assert.That(fileSystem.ReadAllText("/game/assets/levels/other.prefab")).IsEqualTo(before);
    }

    [Test]
    public async Task a_mesh_whose_texture_uri_stops_resolving_is_reported_not_rewritten()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/rust.png");
        var glb = GlbBinary.Write(new System.Text.Json.Nodes.JsonObject
        {
            ["images"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["uri"] = "../textures/rust.png", ["mimeType"] = "image/png" }),
        }, []);
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", glb);
        ProjectVerifierTests.Mint(fileSystem, "/game/assets/models/crate.glb");

        var result = AssetMover.Move(fileSystem, s_layout, "/game/assets/textures/rust.png", "/game/assets/textures/metal/rust.png");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Warnings.Count).IsEqualTo(1);
        await Assert.That(result.Warnings[0]).Contains("models/crate.glb");
        await Assert.That(result.Warnings[0]).Contains("../textures/rust.png");
        await Assert.That(result.Warnings[0]).Contains("re-export");
    }

    [Test]
    public async Task a_mesh_uri_that_was_already_broken_is_not_blamed_on_the_move()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/rust.png");
        var glb = GlbBinary.Write(new System.Text.Json.Nodes.JsonObject
        {
            ["images"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["uri"] = "../textures/gone.png", ["mimeType"] = "image/png" }),
        }, []);
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", glb);
        ProjectVerifierTests.Mint(fileSystem, "/game/assets/models/crate.glb");

        var unrelated = AssetMover.Move(fileSystem, s_layout, "/game/assets/textures/rust.png", "/game/assets/textures/metal/rust.png");
        var meshItself = AssetMover.Move(fileSystem, s_layout, "/game/assets/models/crate.glb", "/game/assets/props/crate.glb");

        await Assert.That(unrelated.Warnings).IsEmpty();
        // Moving the mesh changes where every relative uri points, so that one IS this move's.
        await Assert.That(meshItself.Warnings.Count).IsEqualTo(1);
        await Assert.That(meshItself.Warnings[0]).Contains("props/crate.glb");
    }

    [Test]
    public async Task a_recorded_mesh_uri_follows_its_moved_texture()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteCarried(fileSystem, "/game/assets/textures/rust.png", "png");
        var rust = SidecarMeta.Load(fileSystem, "/game/assets/textures/rust.png.meta").Guid;
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", MeshContainerTests.Glb("""{"images":[{"uri":"../textures/rust.png"}]}"""));
        ProjectVerifierTests.Mint(fileSystem, "/game/assets/models/crate.glb", s_crate);
        MeshReferencesTests.Record(fileSystem, "/game/assets/models/crate.glb", "images[0]", "../textures/rust.png", new Paradise.Authoring.AssetReference(rust, "textures/rust.png"));

        var result = AssetMover.Move(fileSystem, s_layout, "/game/assets/textures/rust.png", "/game/assets/textures/metal/rust.png");

        await Assert.That(result.Warnings).IsEmpty();
        await Assert.That(result.Rewritten).IsEquivalentTo(new[] { "models/crate.glb" });
        var image = MeshReferencesTests.Image(fileSystem, "/game/assets/models/crate.glb");
        await Assert.That(image.Uri).IsEqualTo("../textures/metal/rust.png");
        await Assert.That(image.Reference!.Path).IsEqualTo("textures/metal/rust.png");
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
    }

    [Test]
    public async Task a_moved_mesh_has_its_own_uris_relocated()
    {
        // The texture did not move; the mesh did, so every relative uri in it went stale at once.
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteCarried(fileSystem, "/game/assets/textures/rust.png", "png");
        var rust = SidecarMeta.Load(fileSystem, "/game/assets/textures/rust.png.meta").Guid;
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", MeshContainerTests.Glb("""{"images":[{"uri":"../textures/rust.png"}]}"""));
        ProjectVerifierTests.Mint(fileSystem, "/game/assets/models/crate.glb", s_crate);
        MeshReferencesTests.Record(fileSystem, "/game/assets/models/crate.glb", "images[0]", "../textures/rust.png", new Paradise.Authoring.AssetReference(rust, "textures/rust.png"));

        var result = AssetMover.Move(fileSystem, s_layout, "/game/assets/models/crate.glb", "/game/assets/props/box/crate.glb");

        await Assert.That(result.Warnings).IsEmpty();
        var image = MeshReferencesTests.Image(fileSystem, "/game/assets/props/box/crate.glb");
        await Assert.That(image.Uri).IsEqualTo("../../textures/rust.png");
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
    }

    [Test]
    public async Task a_document_that_cannot_be_rewritten_is_an_error_after_the_files_moved()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("read-only bits are a Unix notion here");

        var root = Path.Combine(Path.GetTempPath(), $"paradise_mv_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "assets", "models"));
        Directory.CreateDirectory(Path.Combine(root, "assets", "levels"));
        File.WriteAllText(Path.Combine(root, "assets", "project.toml"), "name = \"x\"\nschema_version = 1\n");
        var locked = Path.Combine(root, "assets", "levels", "district.prefab");
        try
        {
            using var physical = new Zio.FileSystems.PhysicalFileSystem();
            var layout = new AssetProjectLayout(physical.ConvertPathFromInternal(root));
            physical.WriteAllBytes(layout.Assets / "models/crate.glb", [1]);
            new SidecarMeta(s_crate) { Importer = "mesh" }.Save(physical, layout.Assets / "models/crate.glb.meta");
            var level = new PrefabDocument();
            var top = PrefabObject.WithMeta(s_root, "crate_01");
            top.Components.Add(new PrefabComponent(new Guid("bdc4fc87-d7b4-41f1-bc90-fc827005adfc"), "Renderable",
                new CanonicalTomlTable { { "Mesh", AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(s_crate, "models/crate.glb")) } }));
            level.Objects.Add(top);
            PrefabDocumentSerializer.Save(physical, layout.Assets / "levels/district.prefab", level);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(locked, UnixFileMode.UserRead);

            var result = AssetMover.Move(physical, layout, layout.Assets / "models/crate.glb", layout.Assets / "models/box.glb");

            await Assert.That(result.Succeeded).IsFalse();
            await Assert.That(result.Moved).IsEquivalentTo(new[] { "models/box.glb" });
            await Assert.That(result.Errors.Count).IsEqualTo(1);
            await Assert.That(result.Errors[0]).Contains("levels/district.prefab");
            await Assert.That(result.Errors[0]).Contains("still name the old path");
        }
        finally
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task a_case_only_rename_lands_on_a_real_disk()
    {
        // MemoryFileSystem is case-sensitive, so this runs on the disk, where macOS and Windows
        // report the destination as existing and a direct move is refused (or lands inside).
        var root = Path.Combine(Path.GetTempPath(), $"paradise_mv_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "assets", "Models"));
        Directory.CreateDirectory(Path.Combine(root, "assets", "levels"));
        File.WriteAllText(Path.Combine(root, "assets", "project.toml"), "name = \"x\"\nschema_version = 1\n");
        try
        {
            using var physical = new Zio.FileSystems.PhysicalFileSystem();
            var layout = new AssetProjectLayout(physical.ConvertPathFromInternal(root));
            physical.WriteAllBytes(layout.Assets / "Models/crate.glb", [1]);
            new SidecarMeta(s_crate) { Importer = "mesh" }.Save(physical, layout.Assets / "Models/crate.glb.meta");
            var level = new PrefabDocument();
            var top = PrefabObject.WithMeta(s_root, "crate_01");
            top.Components.Add(new PrefabComponent(new Guid("bdc4fc87-d7b4-41f1-bc90-fc827005adfc"), "Renderable",
                new CanonicalTomlTable { { "Mesh", AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(s_crate, "Models/crate.glb")) } }));
            level.Objects.Add(top);
            PrefabDocumentSerializer.Save(physical, layout.Assets / "levels/district.prefab", level);

            var directory = AssetMover.Move(physical, layout, layout.Assets / "Models", layout.Assets / "models");
            var file = AssetMover.Move(physical, layout, layout.Assets / "models/crate.glb", layout.Assets / "models/Crate.glb");

            await Assert.That(directory.Errors).IsEmpty();
            await Assert.That(file.Errors).IsEmpty();
            await Assert.That(Directory.EnumerateDirectories(Path.Combine(root, "assets")).Select(Path.GetFileName)).Contains("models");
            await Assert.That(Directory.EnumerateFiles(Path.Combine(root, "assets", "models")).Select(f => Path.GetFileName(f)!)).IsEquivalentTo(new[] { "Crate.glb", "Crate.glb.meta" });
            await Assert.That(File.ReadAllText(Path.Combine(root, "assets", "levels", "district.prefab"))).Contains("path = \"models/Crate.glb\"");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [Arguments("/game/assets/models/crate.glb.meta", "/game/assets/models/box.glb.meta", "sidecar")]
    [Arguments("/game/assets/models/missing.glb", "/game/assets/models/box.glb", "does not exist")]
    [Arguments("/game/assets/models/crate.glb", "/game/assets/models/taken.glb", "already exists")]
    [Arguments("/game/assets/models/crate.glb", "/game/build/crate.glb", "not under")]
    [Arguments("/game/assets/models/crate.glb", "/game/assets/models/crate", "no extension")]
    [Arguments("/game/assets/models", "/game/assets/models/inner", "into itself")]
    [Arguments("/game/assets/project.toml", "/game/assets/other.toml", "project.toml")]
    public async Task what_mv_refuses_it_refuses_before_touching_anything(string from, string to, string reason)
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/taken.glb");
        var files = AssetIndex.Scan(fileSystem, "/game/assets").Files.Select(f => f.FullName).ToList();

        var result = AssetMover.Move(fileSystem, s_layout, from, to);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains(reason);
        await Assert.That(AssetIndex.Scan(fileSystem, "/game/assets").Files.Select(f => f.FullName)).IsEquivalentTo(files);
    }

    private static void WriteLevel(MemoryFileSystem fileSystem, UPath path, string meshPath)
    {
        var document = new PrefabDocument();
        var root = PrefabObject.WithMeta(s_root, "crate_01");
        root.Components.Add(new PrefabComponent(
            new Guid("bdc4fc87-d7b4-41f1-bc90-fc827005adfc"), "Renderable",
            new CanonicalTomlTable { { "Mesh", AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(s_crate, meshPath)) } }));
        document.Objects.Add(root);
        PrefabDocumentSerializer.Save(fileSystem, path, document);
        ProjectVerifierTests.MintDocumentSidecar(fileSystem, path);
    }
}
