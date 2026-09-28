using TUnit.Assertions.Enums;
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
        await Assert.That(text).Contains("[glb]");
        await Assert.That(text).Contains("references = [{ slot = \"images[0]\", uri = \"../textures/rust.png\", guid = \"");
        await Assert.That(MeshReferences.Recorded(fileSystem, Mesh)).IsEquivalentTo(new[]
        {
            new MeshReference("images[0]", "../textures/rust.png", new AssetReference(rust, "textures/rust.png")),
        }, CollectionOrdering.Matching);
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
    public async Task a_moved_texture_is_followed_by_its_recorded_guid_and_the_container_is_never_written()
    {
        // The entry keeps the uri the container spells: recording another left sidecar and
        // container disagreeing, which the next pass read as a re-export and dropped the entry —
        // losing the identity recorded to survive exactly this rename.
        using var fileSystem = ProjectVerifierTests.CreateProject();
        Texture(fileSystem, "/game/assets/textures/metal/rust.png", out var rust);
        WriteMesh(fileSystem, Mesh, """{"images":[{"uri":"../textures/rust.png"}]}""");
        Record(fileSystem, Mesh, "images[0]", "../textures/rust.png", new AssetReference(rust, "textures/rust.png"));
        var container = fileSystem.ReadAllBytes(Mesh);

        var first = MeshReferences.Reconcile(fileSystem, Index(fileSystem), Mesh);
        await Assert.That(first.Changes.Single()).Contains("textures/rust.png -> textures/metal/rust.png");
        await Assert.That(MeshReferences.Apply(fileSystem, Mesh, first)).IsNotNull();
        await Assert.That(MeshReferences.Apply(fileSystem, Mesh, MeshReferences.Reconcile(fileSystem, Index(fileSystem), Mesh))).IsNull();

        var recorded = MeshReferences.Recorded(fileSystem, Mesh);
        await Assert.That(recorded.Count).IsEqualTo(1);
        await Assert.That(recorded[0].Uri).IsEqualTo("../textures/rust.png");
        await Assert.That(recorded[0].Reference).IsEqualTo(new AssetReference(rust, "textures/metal/rust.png"));
        await Assert.That(fileSystem.ReadAllBytes(Mesh)).IsEquivalentTo(container, CollectionOrdering.Matching);
        // Verify follows the guid, and a build does too.
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
        var result = new BuildRunner(fileSystem, s_layout, new BuildRunnerTests.FakeEncoder()).Run();
        await Assert.That(result.Succeeded).IsTrue();
    }

    [Test]
    public async Task a_uri_that_differs_only_in_escaping_is_not_a_change()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        Texture(fileSystem, "/game/assets/textures/a b.png", out var texture);
        WriteMesh(fileSystem, Mesh, """{"images":[{"uri":"../textures/a b.png"}]}""");
        Record(fileSystem, Mesh, "images[0]", "../textures/a b.png", new AssetReference(texture, "textures/a b.png"));

        var reconciliation = MeshReferences.Reconcile(fileSystem, Index(fileSystem), Mesh);

        await Assert.That(reconciliation.SidecarChanged).IsFalse();
        await Assert.That(reconciliation.Changes).IsEmpty();
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
    }

    [Test]
    public async Task a_duplicate_slot_is_a_verify_error_and_nothing_else_throws()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        Texture(fileSystem, "/game/assets/textures/rust.png", out var rust);
        WriteMesh(fileSystem, Mesh, """{"images":[{"uri":"../textures/rust.png"}]}""");
        var meta = SidecarMeta.Load(fileSystem, Mesh + ".meta");
        var entry = new CanonicalInlineTable
        {
            { "slot", "images[0]" }, { "uri", "../textures/rust.png" }, { "guid", DocumentGuid.Format(rust) }, { "path", "textures/rust.png" },
        };
        meta.SetSetting(GlbImportSettings.Domain, new CanonicalTomlTable { { "references", new List<object> { entry, entry } } });
        meta.Save(fileSystem, Mesh + ".meta");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Select(f => f.Message)).Contains(m => m.Contains("twice"));
        await Assert.That(MeshReferences.Recorded(fileSystem, Mesh).Count).IsEqualTo(2);
        await Assert.That(ReferenceGraph.Build(fileSystem, s_layout, Index(fileSystem)).DependentsOf(rust).Count).IsEqualTo(1);
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

        MeshReferences.Apply(fileSystem, Mesh, MeshReferences.Reconcile(fileSystem, Index(fileSystem), Mesh));

        await Assert.That(MeshReferences.Recorded(fileSystem, Mesh)).IsEmpty();
        await Assert.That(fileSystem.ReadAllText(Mesh + ".meta")).DoesNotContain("[glb]");
    }

    [Test]
    public async Task a_malformed_entry_is_a_verify_error_naming_the_domain()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        WriteMesh(fileSystem, Mesh, """{"images":[]}""");
        var meta = SidecarMeta.Load(fileSystem, Mesh + ".meta");
        meta.SetSetting(GlbImportSettings.Domain, new CanonicalTomlTable { { "references", new List<object> { new CanonicalInlineTable { { "slot", "images[0]" } } } } });
        meta.Save(fileSystem, Mesh + ".meta");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(findings[0].Message).Contains("[glb]");
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
        var entries = GlbImportSettings.Read(meta).Where(entry => entry.Slot != slot).ToList();
        entries.Add(new MeshReference(slot, uri, reference));
        GlbImportSettings.Write(meta, entries);
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
