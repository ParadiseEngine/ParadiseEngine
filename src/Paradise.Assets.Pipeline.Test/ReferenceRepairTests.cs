using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Authoring;

namespace Paradise.Assets.Pipeline.Test;

public class ReferenceRepairTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    private const string Document = "/game/assets/levels/district.prefab";

    [Test]
    public async Task a_stale_path_is_caught_up_to_where_its_guid_now_lives()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var guid = WriteMaterial(fileSystem, "/game/assets/materials/patina.toml");
        WriteReference(fileSystem, new AssetReference(guid, "materials/rust.toml"));

        var repaired = ReferenceRepair.Fix(fileSystem, s_layout);

        await Assert.That(repaired.Count).IsEqualTo(1);
        await Assert.That(repaired[0].Path).IsEqualTo(new UPath(Document));
        await Assert.That(repaired[0].Repointed).Contains("materials/rust.toml -> materials/patina.toml");
        await Assert.That(ReferenceIn(fileSystem).Path).IsEqualTo("materials/patina.toml");
    }

    [Test]
    public async Task the_fix_changes_the_path_and_never_the_identity()
    {
        // A fix that re-minted or dropped the guid would turn "the text is out of date" into
        // "the reference now means something else".
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var guid = WriteMaterial(fileSystem, "/game/assets/materials/patina.toml");
        WriteReference(fileSystem, new AssetReference(guid, "materials/rust.toml"));

        ReferenceRepair.Fix(fileSystem, s_layout);

        await Assert.That(ReferenceIn(fileSystem).Guid).IsEqualTo(guid);
    }

    [Test]
    public async Task verify_is_clean_after_a_fix()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var guid = WriteMaterial(fileSystem, "/game/assets/materials/patina.toml");
        WriteReference(fileSystem, new AssetReference(guid, "materials/rust.toml"));

        ReferenceRepair.Fix(fileSystem, s_layout);

        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
    }

    [Test]
    public async Task a_fixed_document_is_canonical()
    {
        // The whole point of fixing rather than leaving the warning: the tree must still pass
        // prefab-check, so the rewrite goes through the canonical writer like every machine edit.
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var guid = WriteMaterial(fileSystem, "/game/assets/materials/patina.toml");
        WriteReference(fileSystem, new AssetReference(guid, "materials/rust.toml"));

        ReferenceRepair.Fix(fileSystem, s_layout);

        var results = PrefabCheck.Run(fileSystem, s_layout);
        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Outcome).IsEqualTo(PrefabCheckOutcome.Canonical);
    }

    [Test]
    public async Task a_reference_that_already_names_its_asset_is_not_rewritten()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var guid = WriteMaterial(fileSystem, "/game/assets/materials/rust.toml");
        WriteReference(fileSystem, new AssetReference(guid, "materials/rust.toml"));
        var before = fileSystem.ReadAllText(Document);

        await Assert.That(ReferenceRepair.Fix(fileSystem, s_layout)).IsEmpty();
        await Assert.That(fileSystem.ReadAllText(Document)).IsEqualTo(before);
    }

    [Test]
    public async Task a_guid_no_asset_carries_is_left_alone_for_verify_to_report()
    {
        // There is nowhere to point it at. Rewriting the path to anything here would replace a
        // named error with a reference that quietly means something else.
        using var fileSystem = ProjectVerifierTests.CreateProject();
        WriteMaterial(fileSystem, "/game/assets/materials/rust.toml");
        WriteReference(fileSystem, new AssetReference(Guid.NewGuid(), "materials/rust.toml"));
        var before = fileSystem.ReadAllText(Document);

        await Assert.That(ReferenceRepair.Fix(fileSystem, s_layout)).IsEmpty();
        await Assert.That(fileSystem.ReadAllText(Document)).IsEqualTo(before);
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)[0].Severity).IsEqualTo(VerifySeverity.Error);
    }

    [Test]
    public async Task an_unstamped_glb_is_stamped_from_its_uri()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteCarried(fileSystem, "/game/assets/textures/rust.png", "png");
        var rust = SidecarMeta.Load(fileSystem, "/game/assets/textures/rust.png.meta").Guid;
        ProjectVerifierTests.WriteCarried(fileSystem, "/game/assets/models/crate.glb", "");
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", GlbTextureReferencesTests.Glb("""{"images":[{"uri":"../textures/rust.png"}]}"""));

        var repaired = ReferenceRepair.Fix(fileSystem, s_layout);

        await Assert.That(repaired.Count).IsEqualTo(1);
        await Assert.That(repaired[0].Repointed[0]).Contains("stamped as textures/rust.png");
        var image = GlbTextureReferences.Read(fileSystem.ReadAllBytes("/game/assets/models/crate.glb"))[0];
        await Assert.That(image.Reference).IsEqualTo(new AssetReference(rust, "textures/rust.png"));
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
    }

    [Test]
    public async Task a_glb_whose_texture_moved_has_its_uri_caught_up()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteCarried(fileSystem, "/game/assets/textures/metal/rust.png", "png");
        var rust = SidecarMeta.Load(fileSystem, "/game/assets/textures/metal/rust.png.meta").Guid;
        ProjectVerifierTests.WriteCarried(fileSystem, "/game/assets/models/crate.glb", "");
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", GlbTextureReferences.Stamp(
            GlbTextureReferencesTests.Glb("""{"images":[{"uri":"../textures/rust.png"}]}"""),
            _ => new AssetReference(rust, "textures/rust.png")));

        var repaired = ReferenceRepair.Fix(fileSystem, s_layout);

        await Assert.That(repaired.Count).IsEqualTo(1);
        await Assert.That(repaired[0].Repointed).Contains("textures/rust.png -> textures/metal/rust.png");
        var image = GlbTextureReferences.Read(fileSystem.ReadAllBytes("/game/assets/models/crate.glb"))[0];
        await Assert.That(image.Uri).IsEqualTo("../textures/metal/rust.png");
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
    }

    [Test]
    public async Task a_stale_prefab_instance_reference_is_caught_up_too()
    {
        // The instance's prefab is a typed field rather than a payload table, so it takes a
        // different branch of the walk and was the half a copied rewrite would miss.
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteCanonicalDocument(fileSystem, "/game/assets/prefabs/barrel.prefab");
        var guid = SidecarMeta.Load(fileSystem, "/game/assets/prefabs/barrel.prefab.meta").Guid;

        var scene = new PrefabDocument();
        var instance = PrefabObject.WithMeta(Guid.NewGuid(), "barrel_01");
        instance.Prefab = new AssetReference(guid, "prefabs/crate.prefab");
        scene.Objects.Add(instance);
        fileSystem.CreateDirectory("/game/assets/levels");
        PrefabDocumentSerializer.Save(fileSystem, Document, scene);
        ProjectVerifierTests.MintDocumentSidecar(fileSystem, Document);

        var repaired = ReferenceRepair.Fix(fileSystem, s_layout);

        await Assert.That(repaired.Count).IsEqualTo(1);
        var fixedUp = PrefabDocumentSerializer.Load(fileSystem, Document);
        await Assert.That(fixedUp.Objects[0].Prefab!.Path).IsEqualTo("prefabs/barrel.prefab");
        await Assert.That(fixedUp.Objects[0].Prefab!.Guid).IsEqualTo(guid);
    }

    [Test]
    public async Task a_stale_path_inside_an_array_is_caught_up()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var guid = WriteMaterial(fileSystem, "/game/assets/materials/patina.toml");
        ProjectVerifierTests.WriteDocumentWith(fileSystem, Document, new CanonicalTomlTable
        {
            { "Slots", new object[] { AssetReferenceCodec.Write(new AssetReference(guid, "materials/rust.toml")) } },
        });

        await Assert.That(ReferenceRepair.Fix(fileSystem, s_layout).Count).IsEqualTo(1);
        await Assert.That(fileSystem.ReadAllText(Document)).Contains("materials/patina.toml");
    }

    [Test]
    public async Task an_unparseable_document_is_left_for_verify_rather_than_failing_the_pass()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteDocument(fileSystem, Document, "not toml at all [[[");

        await Assert.That(ReferenceRepair.Fix(fileSystem, s_layout)).IsEmpty();
    }

    private static Guid WriteMaterial(MemoryFileSystem fileSystem, UPath path)
    {
        ProjectVerifierTests.WriteDocument(fileSystem, path, "a = 1\n");
        return SidecarMeta.Load(fileSystem, SidecarMeta.PathFor(path)).Guid;
    }

    private static void WriteReference(MemoryFileSystem fileSystem, AssetReference reference)
        => ProjectVerifierTests.WriteDocumentWith(fileSystem, Document, new CanonicalTomlTable
        {
            { "Material", AssetReferenceCodec.Write(reference) },
        });

    private static AssetReference ReferenceIn(MemoryFileSystem fileSystem)
    {
        var document = PrefabDocumentSerializer.Load(fileSystem, Document);
        var table = (CanonicalInlineTable)document.Objects[0].Components[1].Data.Value("Material")!;
        return AssetReferenceCodec.TryRead(table, out var reference)
            ? reference
            : throw new InvalidOperationException("the fixture's reference did not survive the fix");
    }
}
