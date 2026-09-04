using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// A mesh's references live in its SIDECAR, resolved from the uris its container spells, so a
/// format nobody can edit (FBX) gets the same identity story as a GLB. The one rule pinned here:
/// the recorded guid wins while the container still spells the uri it was recorded from, and the
/// uri wins the moment a re-export changed it.
/// </summary>
public class MeshReferencesTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    private const string Mesh = "/game/assets/models/crate.glb";

    [Test]
    public async Task the_domain_round_trips_through_the_sidecar_bytes()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        Texture(fileSystem, "/game/assets/textures/rust.png", out var rust);
        WriteMesh(fileSystem, Mesh, """{"images":[{"uri":"../textures/rust.png"}]}""");

        Record(fileSystem, Mesh, "images[0]", "../textures/rust.png", new AssetReference(rust, "textures/rust.png"));

        var text = fileSystem.ReadAllText(Mesh + ".meta");
        await Assert.That(text).Contains("[mesh]");
        await Assert.That(text).Contains("references = [{ slot = \"images[0]\", uri = \"../textures/rust.png\", guid = \"");
        await Assert.That(MeshReferences.Recorded(fileSystem, Mesh)).IsEquivalentTo(new[]
        {
            new MeshReference("images[0]", "../textures/rust.png", new AssetReference(rust, "textures/rust.png")),
        });
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
    }

    [Test]
    public async Task an_unrecorded_uri_naming_an_identified_texture_is_recorded()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        Texture(fileSystem, "/game/assets/textures/rust.png", out var rust);
        WriteMesh(fileSystem, Mesh, """{"images":[{"uri":"../textures/rust.png"}]}""");

        var reconciliation = MeshReferences.Reconcile(fileSystem, Index(fileSystem), Mesh);

        await Assert.That(reconciliation.SidecarChanged).IsTrue();
        await Assert.That(reconciliation.References[0].Reference).IsEqualTo(new AssetReference(rust, "textures/rust.png"));
        await Assert.That(reconciliation.Changes[0]).Contains("recorded as textures/rust.png");
    }

    [Test]
    public async Task a_recorded_guid_wins_when_the_texture_moved_and_the_uri_is_caught_up()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        Texture(fileSystem, "/game/assets/textures/metal/rust.png", out var rust);
        WriteMesh(fileSystem, Mesh, """{"images":[{"uri":"../textures/rust.png"}]}""");
        Record(fileSystem, Mesh, "images[0]", "../textures/rust.png", new AssetReference(rust, "textures/rust.png"));

        var reconciliation = MeshReferences.Reconcile(fileSystem, Index(fileSystem), Mesh);
        MeshReferences.Apply(fileSystem, Mesh, reconciliation, rewriteContainer: true);

        await Assert.That(reconciliation.Changes[0]).Contains("textures/rust.png -> textures/metal/rust.png");
        await Assert.That(MeshContainer.Read(Mesh, fileSystem.ReadAllBytes(Mesh))[0].Uri).IsEqualTo("../textures/metal/rust.png");
        var recorded = MeshReferences.Recorded(fileSystem, Mesh)[0];
        await Assert.That(recorded.Reference).IsEqualTo(new AssetReference(rust, "textures/metal/rust.png"));
        await Assert.That(recorded.Uri).IsEqualTo("../textures/metal/rust.png");
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
    }

    [Test]
    public async Task a_changed_uri_wins_because_only_a_re_export_can_change_it()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        Texture(fileSystem, "/game/assets/textures/rust.png", out var rust);
        Texture(fileSystem, "/game/assets/textures/patina.png", out var patina);
        WriteMesh(fileSystem, Mesh, """{"images":[{"uri":"../textures/patina.png"}]}""");
        Record(fileSystem, Mesh, "images[0]", "../textures/rust.png", new AssetReference(rust, "textures/rust.png"));

        var reconciliation = MeshReferences.Reconcile(fileSystem, Index(fileSystem), Mesh);

        await Assert.That(reconciliation.References[0].Reference).IsEqualTo(new AssetReference(patina, "textures/patina.png"));
        await Assert.That(reconciliation.Changes[0]).Contains("re-exported");
    }

    [Test]
    public async Task a_recorded_guid_nobody_carries_is_kept_for_verify_to_name()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        WriteMesh(fileSystem, Mesh, """{"images":[{"uri":"../textures/rust.png"}]}""");
        var gone = new AssetReference(Guid.NewGuid(), "textures/rust.png");
        Record(fileSystem, Mesh, "images[0]", "../textures/rust.png", gone);

        var reconciliation = MeshReferences.Reconcile(fileSystem, Index(fileSystem), Mesh);

        await Assert.That(reconciliation.SidecarChanged).IsFalse();
        await Assert.That(reconciliation.References[0].Reference).IsEqualTo(gone);
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout).Select(f => f.Severity)).Contains(VerifySeverity.Error);
    }

    [Test]
    public async Task a_slot_the_container_no_longer_has_loses_its_entry_and_an_empty_list_drops_the_domain()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        Texture(fileSystem, "/game/assets/textures/rust.png", out var rust);
        WriteMesh(fileSystem, Mesh, """{"images":[]}""");
        Record(fileSystem, Mesh, "images[0]", "../textures/rust.png", new AssetReference(rust, "textures/rust.png"));

        MeshReferences.Apply(fileSystem, Mesh, MeshReferences.Reconcile(fileSystem, Index(fileSystem), Mesh), rewriteContainer: false);

        await Assert.That(MeshReferences.Recorded(fileSystem, Mesh)).IsEmpty();
        await Assert.That(fileSystem.ReadAllText(Mesh + ".meta")).DoesNotContain("[mesh]");
    }

    [Test]
    public async Task a_malformed_entry_is_a_verify_error_naming_the_domain()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        WriteMesh(fileSystem, Mesh, """{"images":[]}""");
        var meta = SidecarMeta.Load(fileSystem, Mesh + ".meta");
        meta.SetSetting(MeshImportSettings.Domain, new CanonicalTomlTable { { "references", new List<object> { new CanonicalInlineTable { { "slot", "images[0]" } } } } });
        meta.Save(fileSystem, Mesh + ".meta");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(findings[0].Message).Contains("[mesh]");
    }

    private static AssetIndex Index(MemoryFileSystem fileSystem) => AssetIndex.Scan(fileSystem, s_layout.Assets);

    internal static void Texture(MemoryFileSystem fileSystem, UPath path, out Guid guid)
    {
        ProjectVerifierTests.WriteCarried(fileSystem, path, "png");
        guid = SidecarMeta.Load(fileSystem, SidecarMeta.PathFor(path)).Guid;
    }

    /// <summary>A GLB with its sidecar minted.</summary>
    internal static void WriteMesh(MemoryFileSystem fileSystem, UPath path, string json)
    {
        fileSystem.CreateDirectory(path.GetDirectory());
        fileSystem.WriteAllBytes(path, MeshContainerTests.Glb(json));
        if (!fileSystem.FileExists(SidecarMeta.PathFor(path))) ProjectVerifierTests.Mint(fileSystem, path);
    }

    /// <summary>Records one reference in the mesh's sidecar, minting the sidecar first when it has none.</summary>
    internal static void Record(MemoryFileSystem fileSystem, UPath mesh, string slot, string uri, AssetReference reference)
    {
        var sidecar = SidecarMeta.PathFor(mesh);
        var meta = fileSystem.FileExists(sidecar) ? SidecarMeta.Load(fileSystem, sidecar) : SidecarMeta.Mint();
        var entries = MeshImportSettings.Read(meta).Where(entry => entry.Slot != slot).ToList();
        entries.Add(new MeshReference(slot, uri, reference));
        MeshImportSettings.Write(meta, entries);
        meta.Save(fileSystem, sidecar);
    }

    /// <summary>The container's first uri and the identity recorded for it, as the older stamp tests read them.</summary>
    internal static (string Uri, AssetReference? Reference) Image(MemoryFileSystem fileSystem, UPath mesh)
    {
        var named = MeshContainer.Read(mesh, fileSystem.ReadAllBytes(mesh))[0];
        var recorded = MeshReferences.Recorded(fileSystem, mesh).FirstOrDefault(entry => entry.Slot == named.Slot);
        return (named.Uri, recorded.Slot is null ? null : recorded.Reference);
    }
}
