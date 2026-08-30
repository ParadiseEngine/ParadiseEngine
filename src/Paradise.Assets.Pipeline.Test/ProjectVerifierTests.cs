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
        WriteDocument(fileSystem, "/game/assets/scenes/bad.scene", "schema_version = 1\nobjects = 3\n");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
    }

    [Test]
    public async Task a_non_canonical_scene_is_a_warning_not_an_error()
    {
        using var fileSystem = CreateProject();
        // Valid, but spaced the way a person types and a machine never writes.
        WriteDocument(
            fileSystem,
            "/game/assets/scenes/edited.scene",
            "schema_version=1\n\n[[objects]]\n\n[[objects.components]]\n" +
            $"id = \"{DocumentGuid.Format(WellKnownComponents.MetaId)}\"\ntype = \"meta\"\n" +
            "Guid = \"1c9a2f4e-0d3b-4c5a-8e6f-7a8b9c0d1e2f\"\n");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Warning);
        await Assert.That(findings[0].Message).Contains("canonical");
    }

    [Test]
    public async Task an_unknown_file_kind_is_a_warning()
    {
        using var fileSystem = CreateProject();
        WriteCarried(fileSystem, "/game/assets/notes.txt", "todo");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Warning);
    }

    [Test]
    public async Task the_manifest_needs_a_sidecar_like_everything_else()
    {
        using var fileSystem = CreateProject();
        fileSystem.DeleteFile(SidecarMeta.PathFor(s_layout.Manifest));

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Message).Contains("no sidecar");
    }

    [Test]
    public async Task a_stale_hash_is_a_warning_not_an_error()
    {
        // Every legitimate edit makes the recorded hash stale. Erroring would keep the tree red
        // and teach everybody to ignore the one signal that says "this asset moved on".
        using var fileSystem = CreateProject();
        fileSystem.CreateDirectory("/game/assets/models");
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", [1, 2, 3]);
        new SidecarMeta(Guid.NewGuid(), SidecarAssetKind.Mesh) { Hash = new string('a', 64) }
            .Save(fileSystem, "/game/assets/models/crate.glb.meta");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Warning);
        await Assert.That(findings[0].Message).Contains("changed since");
    }

    [Test]
    public async Task a_matching_hash_is_silent()
    {
        using var fileSystem = CreateProject();
        fileSystem.CreateDirectory("/game/assets/models");
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", [1, 2, 3]);
        new SidecarMeta(Guid.NewGuid(), SidecarAssetKind.Mesh)
        {
            Hash = SidecarMeta.ComputeHash(new byte[] { 1, 2, 3 }),
        }.Save(fileSystem, "/game/assets/models/crate.glb.meta");

        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout).Count).IsEqualTo(0);
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
        WriteCarried(fileSystem, "/game/assets/zz-notes.txt", "todo");
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
        // The manifest is an asset like everything else under assets/, so it carries an identity
        // too -- the only thing that does not is a sidecar, because one describing a sidecar is
        // an infinite regress.
        WriteDocument(fileSystem, "/game/assets/project.toml", "name = \"shiningpie\"\nschema_version = 1\n");
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
        var document = new SceneDocument();
        document.Objects.Add(SceneObject.WithMeta(Guid.NewGuid(), "crate"));
        fileSystem.CreateDirectory(path.GetDirectory());
        SceneDocumentSerializer.Save(fileSystem, path, document);
        MintDocumentSidecar(fileSystem, path);
    }

    /// <summary>
    /// Writes a text document AND its sidecar.
    /// </summary>
    /// <remarks>
    /// Every asset carries an identity and identity lives in the sidecar, documents included — so
    /// a fixture that writes a scene without one is not a scene, it is a `verify` error. Which is
    /// exactly what this helper exists to stop a test from asserting by accident.
    /// </remarks>
    internal static void WriteDocument(MemoryFileSystem fileSystem, UPath path, string text)
    {
        fileSystem.CreateDirectory(path.GetDirectory());
        fileSystem.WriteAllText(path, text);
        MintDocumentSidecar(fileSystem, path);
    }

    internal static void MintDocumentSidecar(MemoryFileSystem fileSystem, UPath path)
        => SidecarMeta.Mint(SidecarAssetKind.Document).Save(fileSystem, SidecarMeta.PathFor(path));

    /// <summary>
    /// Writes a file the pipeline has no opinion about, plus its sidecar.
    /// </summary>
    /// <remarks>
    /// "The pipeline does not process this" and "this is not an asset" are different statements,
    /// and only the first is true of a stray .txt — so it still carries an identity, and a
    /// fixture that omits one is testing a missing sidecar rather than whatever it meant to.
    /// </remarks>
    internal static void WriteCarried(MemoryFileSystem fileSystem, UPath path, string text)
    {
        fileSystem.CreateDirectory(path.GetDirectory());
        fileSystem.WriteAllText(path, text);
        SidecarMeta.Mint(SidecarAssetKind.Opaque).Save(fileSystem, SidecarMeta.PathFor(path));
    }
}
