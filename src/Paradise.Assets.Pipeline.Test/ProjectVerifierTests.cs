using Paradise.Assets.Documents;
using Paradise.Assets.Project;

namespace Paradise.Assets.Pipeline.Test;

public class ProjectVerifierTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    [Test]
    public async Task a_consistent_project_has_no_findings()
    {
        using var fileSystem = CreateProject();
        AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb", SidecarAssetKind.Mesh);
        WriteCanonicalScene(fileSystem, "/game/assets/scenes/district.scene");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(0);
    }

    [Test]
    public async Task a_missing_assets_directory_is_the_only_finding()
    {
        using var fileSystem = new MemoryFileSystem();

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
    }

    [Test]
    public async Task a_foreign_asset_without_a_sidecar_is_an_error()
    {
        using var fileSystem = CreateProject();
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", [1]);

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Message).Contains("no sidecar");
    }

    [Test]
    public async Task an_orphaned_sidecar_is_an_error()
    {
        using var fileSystem = CreateProject();
        SidecarMeta.Mint(SidecarAssetKind.Mesh).Save(fileSystem, "/game/assets/models/gone.glb.meta");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Message).Contains("orphaned");
    }

    [Test]
    public async Task duplicate_sidecar_guids_are_an_error_naming_both_files()
    {
        using var fileSystem = CreateProject();
        var meta = SidecarMeta.Mint(SidecarAssetKind.Mesh);
        fileSystem.WriteAllBytes("/game/assets/models/a.glb", [1]);
        fileSystem.WriteAllBytes("/game/assets/models/b.glb", [2]);
        meta.Save(fileSystem, "/game/assets/models/a.glb.meta");
        meta.Save(fileSystem, "/game/assets/models/b.glb.meta");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Message).Contains("a.glb.meta");
        await Assert.That(findings[0].Path).IsEqualTo(new UPath("/game/assets/models/b.glb.meta"));
    }

    [Test]
    public async Task a_sidecar_kind_contradicting_the_extension_is_an_error()
    {
        using var fileSystem = CreateProject();
        fileSystem.WriteAllBytes("/game/assets/textures/fire.png", [1]);
        SidecarMeta.Mint(SidecarAssetKind.Mesh).Save(fileSystem, "/game/assets/textures/fire.png.meta");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Message).Contains("Texture");
    }

    [Test]
    public async Task an_unparseable_sidecar_is_an_error()
    {
        using var fileSystem = CreateProject();
        fileSystem.WriteAllBytes("/game/assets/models/a.glb", [1]);
        fileSystem.WriteAllText("/game/assets/models/a.glb.meta", "kind = \"mesh\"\n");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Message).Contains("schema_version");
    }

    [Test]
    public async Task an_invalid_scene_is_an_error()
    {
        using var fileSystem = CreateProject();
        fileSystem.WriteAllText("/game/assets/scenes/bad.scene", "schema_version = 1\nobjects = 3\n");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
    }

    [Test]
    public async Task a_non_canonical_scene_is_a_warning_not_an_error()
    {
        using var fileSystem = CreateProject();
        // Valid, but keys in a non-canonical order — a hand edit.
        fileSystem.WriteAllText(
            "/game/assets/scenes/edited.scene",
            "schema_version = 1\n\n[[objects]]\nname = \"crate\"\nguid = \"1c9a2f4e-0d3b-4c5a-8e6f-7a8b9c0d1e2f\"\n");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Warning);
        await Assert.That(findings[0].Message).Contains("canonical");
    }

    [Test]
    public async Task an_unknown_file_kind_is_a_warning()
    {
        using var fileSystem = CreateProject();
        fileSystem.WriteAllText("/game/assets/notes.txt", "todo");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Warning);
    }

    [Test]
    public async Task a_broken_manifest_is_an_error()
    {
        using var fileSystem = CreateProject();
        fileSystem.WriteAllText(s_layout.Manifest, "name = \"x\"\nschema_version = 99\n");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Path).IsEqualTo(s_layout.Manifest);
    }

    [Test]
    public async Task errors_come_before_warnings()
    {
        using var fileSystem = CreateProject();
        fileSystem.WriteAllText("/game/assets/zz-notes.txt", "todo");
        fileSystem.WriteAllBytes("/game/assets/models/a.glb", [1]);

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(2);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(findings[1].Severity).IsEqualTo(VerifySeverity.Warning);
    }

    internal static MemoryFileSystem CreateProject()
    {
        var fileSystem = new MemoryFileSystem();
        // The standard subtrees, because Zio does not create parents on write and empty
        // directories produce no findings anyway.
        fileSystem.CreateDirectory("/game/assets/models");
        fileSystem.CreateDirectory("/game/assets/textures");
        fileSystem.CreateDirectory("/game/assets/scenes");
        fileSystem.WriteAllText("/game/assets/project.toml", "name = \"shiningpie\"\nschema_version = 1\n");
        return fileSystem;
    }

    internal static void AddAssetWithSidecar(MemoryFileSystem fileSystem, UPath asset, SidecarAssetKind kind)
    {
        fileSystem.CreateDirectory(asset.GetDirectory());
        fileSystem.WriteAllBytes(asset, [1, 2, 3]);
        SidecarMeta.Mint(kind).Save(fileSystem, SidecarMeta.PathFor(asset));
    }

    internal static void WriteCanonicalScene(MemoryFileSystem fileSystem, UPath path)
    {
        fileSystem.CreateDirectory(path.GetDirectory());
        var document = new SceneDocument();
        document.Objects.Add(new SceneObject(Guid.NewGuid(), "crate"));
        SceneDocumentSerializer.Save(fileSystem, path, document);
    }
}
