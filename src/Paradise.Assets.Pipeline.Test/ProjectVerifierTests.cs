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
        AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        WriteCanonicalDocument(fileSystem, "/game/assets/levels/district.prefab");

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
        Mint(fileSystem, "/game/assets/models/gone.glb");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Message).Contains("orphaned");
    }

    [Test]
    public async Task duplicate_sidecar_guids_are_an_error_naming_both_files()
    {
        using var fileSystem = CreateProject();
        var meta = SidecarMeta.Mint();
        meta.Importer = "mesh";
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
    public async Task settings_under_a_domain_no_step_reads_are_a_warning()
    {
        // The format carries settings opaquely, so THIS is where a misspelled domain surfaces —
        // a warning, not an error, because it may equally be a sidecar from a newer pipeline.
        using var fileSystem = CreateProject();
        fileSystem.WriteAllBytes("/game/assets/textures/fire.png", [1]);
        var meta = SidecarMeta.Mint();
        meta.Importer = "texture";
        meta.SetSetting("texure", new CanonicalTomlTable { { "preset", "normal" } });
        meta.Save(fileSystem, "/game/assets/textures/fire.png.meta");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Warning);
        await Assert.That(findings[0].Message).Contains("[texure]");
    }

    [Test]
    public async Task malformed_settings_in_a_known_domain_are_an_error()
    {
        using var fileSystem = CreateProject();
        fileSystem.WriteAllBytes("/game/assets/textures/fire.png", [1]);
        var meta = SidecarMeta.Mint();
        meta.Importer = "texture";
        meta.SetSetting("texture", new CanonicalTomlTable { { "preset", "shiny" } });
        meta.Save(fileSystem, "/game/assets/textures/fire.png.meta");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(findings[0].Message).Contains("\"shiny\"");
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
        WriteDocument(fileSystem, "/game/assets/levels/bad.prefab", "schema_version = 1\nobjects = 3\n");

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
            "/game/assets/levels/edited.prefab",
            "schema_version=1\n\n[[objects]]\n\n[[objects.components]]\n" +
            $"id = \"{DocumentGuid.Format(WellKnownComponents.MetaId)}\"\ntype = \"meta\"\n" +
            "Guid = \"1c9a2f4e-0d3b-4c5a-8e6f-7a8b9c0d1e2f\"\n");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Warning);
        await Assert.That(findings[0].Message).Contains("canonical");
    }

    [Test]
    public async Task a_malformed_reference_is_a_finding_not_a_crash()
    {
        // A hand-edited or half-migrated document. The reference shape is reserved, so this
        // parses as one and then fails to BE one — which must name the file, the component and
        // the field rather than coming out as an unhandled exception through `verify`/`build`.
        using var fileSystem = CreateProject();
        WriteDocumentWith(fileSystem, "/game/assets/levels/district.prefab", new CanonicalTomlTable
        {
            { "Mesh", new CanonicalInlineTable { { "guid", "not-a-uuid" }, { "path", "models/crate.glb" } } },
        });

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(findings[0].Message).Contains("not-a-uuid");
        await Assert.That(findings[0].Message).Contains("game.Mesh.Mesh");
    }

    [Test]
    public async Task a_dangling_reference_inside_an_array_is_an_error()
    {
        // Material slots are THE shape carrying a guid and a path is for, and they live in an
        // array — so a check that only looked at value position missed its primary use case
        // while the bake happily emitted a path nothing wrote.
        using var fileSystem = CreateProject();
        WriteDocumentWith(fileSystem, "/game/assets/levels/district.prefab", new CanonicalTomlTable
        {
            {
                "Slots", new object[]
                {
                    AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(Guid.NewGuid(), "materials/gone.toml")),
                }
            },
        });

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(findings[0].Message).Contains("materials/gone.toml");
        await Assert.That(findings[0].Message).Contains("game.Mesh.Slots[0]");
    }

    [Test]
    public async Task a_payload_table_inside_an_array_is_not_read_as_a_reference()
    {
        // Inside an array the reader wraps EVERY table as inline, so the reference check is
        // gated on the format's own definition of the shape rather than on the model type —
        // otherwise ordinary payload data would be reported as a malformed reference.
        using var fileSystem = CreateProject();
        WriteDocumentWith(fileSystem, "/game/assets/levels/district.prefab", new CanonicalTomlTable
        {
            {
                "Colliders", new object[]
                {
                    new CanonicalInlineTable { { "ShapeType", "Box" }, { "Radius", 2.0 } },
                }
            },
        });

        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
    }

    /// <summary>Writes a canonical one-object document whose single component carries <paramref name="data"/>.</summary>
    internal static void WriteDocumentWith(MemoryFileSystem fileSystem, UPath path, CanonicalTomlTable data)
    {
        var root = PrefabObject.WithMeta(Guid.NewGuid(), "crate");
        root.Components.Add(new PrefabComponent(Guid.NewGuid(), "game.Mesh", data));

        var document = new PrefabDocument();
        document.Objects.Add(root);

        fileSystem.CreateDirectory(path.GetDirectory());
        PrefabDocumentSerializer.Save(fileSystem, path, document);
        MintDocumentSidecar(fileSystem, path);
    }

    // ---- the ignore list and case (#202, #203) --------------------------------------------

    [Test]
    public async Task an_ignored_file_needs_no_sidecar()
    {
        using var fileSystem = CreateProject(ignore: [".DS_Store", "*.tmp", "*.blend1"]);
        fileSystem.WriteAllBytes("/game/assets/models/.DS_Store", [0]);
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb.tmp", [0]);
        fileSystem.WriteAllBytes("/game/assets/models/crate.blend1", [0]);

        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
    }

    [Test]
    public async Task without_an_ignore_list_scratch_files_need_sidecars_like_anything_else()
    {
        // The engine has no opinion about .DS_Store; the project's manifest does.
        using var fileSystem = CreateProject();
        fileSystem.WriteAllBytes("/game/assets/models/.DS_Store", [0]);

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Message).Contains("no sidecar");
    }

    [Test]
    public async Task a_sidecar_minted_for_an_ignored_file_is_an_error_wherever_it_is_seen()
    {
        // Minted on one machine while .DS_Store is gitignored: without this every OTHER checkout
        // reports an orphan and the machine that made it reports nothing.
        using var fileSystem = CreateProject(ignore: [".DS_Store"]);
        fileSystem.WriteAllBytes("/game/assets/models/.DS_Store", [0]);
        Mint(fileSystem, "/game/assets/models/.DS_Store");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(findings[0].Path).IsEqualTo(new UPath("/game/assets/models/.DS_Store.meta"));
        await Assert.That(findings[0].Message).Contains("delete the sidecar");
    }

    [Test]
    public async Task a_reference_with_the_wrong_case_is_a_warning_naming_the_real_file()
    {
        // Case-exactness still matters — the built tree must open on Linux — but the guid names
        // the asset whatever the path's case, so this is a stale spelling to catch up, not a
        // build to refuse.
        using var fileSystem = CreateProject();
        WriteDocument(fileSystem, "/game/assets/materials/Rust.toml", "a = 1\n");
        var guid = SidecarMeta.Load(fileSystem, "/game/assets/materials/Rust.toml.meta").Guid;
        WriteDocumentWith(fileSystem, "/game/assets/levels/district.prefab", new CanonicalTomlTable
        {
            { "Material", AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(guid, "materials/rust.toml")) },
        });

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Warning);
        await Assert.That(findings[0].Message).Contains("materials/Rust.toml");
        await Assert.That(findings[0].Message).Contains("--fix");
    }

    // ---- the path is a hint; the guid decides ---------------------------------------------

    [Test]
    public async Task a_reference_whose_path_a_rename_left_stale_is_a_warning_naming_where_the_asset_went()
    {
        // A Finder rename: the sidecar travelled with the file, so the identity is intact and
        // every document still spells the old path. That used to fail the build.
        using var fileSystem = CreateProject();
        WriteDocument(fileSystem, "/game/assets/materials/patina.toml", "a = 1\n");
        var guid = SidecarMeta.Load(fileSystem, "/game/assets/materials/patina.toml.meta").Guid;
        WriteDocumentWith(fileSystem, "/game/assets/levels/district.prefab", new CanonicalTomlTable
        {
            { "Material", AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(guid, "materials/rust.toml")) },
        });

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Warning);
        await Assert.That(findings[0].Message).Contains("materials/rust.toml");
        await Assert.That(findings[0].Message).Contains("materials/patina.toml");
        await Assert.That(findings[0].Message).Contains(DocumentGuid.Format(guid));
    }

    [Test]
    public async Task a_reference_whose_path_names_a_different_asset_resolves_by_its_guid()
    {
        // Both halves name something real and they disagree. The guid wins — anything else makes
        // a swapped pair of filenames silently repoint every reference at the wrong asset.
        using var fileSystem = CreateProject();
        WriteDocument(fileSystem, "/game/assets/materials/rust.toml", "a = 1\n");
        WriteDocument(fileSystem, "/game/assets/materials/patina.toml", "a = 2\n");
        var patina = SidecarMeta.Load(fileSystem, "/game/assets/materials/patina.toml.meta").Guid;
        WriteDocumentWith(fileSystem, "/game/assets/levels/district.prefab", new CanonicalTomlTable
        {
            { "Material", AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(patina, "materials/rust.toml")) },
        });

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Warning);
        await Assert.That(findings[0].Message).Contains("materials/patina.toml");
    }

    [Test]
    public async Task a_guid_no_asset_carries_is_an_error_even_when_its_path_names_a_real_file()
    {
        using var fileSystem = CreateProject();
        WriteDocument(fileSystem, "/game/assets/materials/rust.toml", "a = 1\n");
        var stranger = Guid.Parse("11111111-2222-4333-8444-555555555555");
        WriteDocumentWith(fileSystem, "/game/assets/levels/district.prefab", new CanonicalTomlTable
        {
            { "Material", AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(stranger, "materials/rust.toml")) },
        });

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(findings[0].Message).Contains(DocumentGuid.Format(stranger));
        await Assert.That(findings[0].Message).Contains("no asset under assets/ carries");
    }

    [Test]
    public async Task a_path_climbing_above_the_root_with_a_guid_nobody_carries_is_a_finding_not_a_crash()
    {
        using var fileSystem = CreateProject();
        var stranger = Guid.Parse("11111111-2222-4333-8444-555555555555");
        WriteDocumentWith(fileSystem, "/game/assets/levels/district.prefab", new CanonicalTomlTable
        {
            { "Material", AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(stranger, "../../../etc/passwd")) },
        });

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(findings[0].Message).Contains("does not name a place under assets/");
    }

    [Test]
    public async Task a_stale_path_naming_a_different_asset_says_which_guid_that_asset_is()
    {
        // A Finder rename and a hand edit of the path alone look identical to the resolver, and
        // --fix reverts the second one; the warning has to say which guid to change instead.
        using var fileSystem = CreateProject();
        WriteDocument(fileSystem, "/game/assets/materials/rust.toml", "a = 1\n");
        WriteDocument(fileSystem, "/game/assets/materials/patina.toml", "a = 2\n");
        var rust = SidecarMeta.Load(fileSystem, "/game/assets/materials/rust.toml.meta").Guid;
        var patina = SidecarMeta.Load(fileSystem, "/game/assets/materials/patina.toml.meta").Guid;
        WriteDocumentWith(fileSystem, "/game/assets/levels/district.prefab", new CanonicalTomlTable
        {
            { "Material", AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(patina, "materials/rust.toml")) },
        });

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Warning);
        await Assert.That(findings[0].Message).Contains(DocumentGuid.Format(rust));
        await Assert.That(findings[0].Message).Contains("change the guid instead");
    }

    [Test]
    public async Task a_reference_into_an_ignored_asset_says_it_is_ignored()
    {
        // The file has no identity by design, not by omission; "no readable identity" sent the
        // author looking for a broken sidecar that was never supposed to exist.
        using var fileSystem = CreateProject(ignore: ["*.blend"]);
        fileSystem.CreateDirectory("/game/assets/models");
        fileSystem.WriteAllText("/game/assets/models/crate.blend", "x");
        WriteDocumentWith(fileSystem, "/game/assets/levels/district.prefab", new CanonicalTomlTable
        {
            { "Source", AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(Guid.NewGuid(), "models/crate.blend")) },
        });

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(findings[0].Message).Contains("ignored by the manifest");
    }

    [Test]
    public async Task an_unrecorded_glb_image_naming_an_identified_texture_is_a_warning()
    {
        using var fileSystem = CreateProject();
        WriteCarried(fileSystem, "/game/assets/textures/rust.png", "png");
        WriteCarried(fileSystem, "/game/assets/models/crate.glb", "");
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", MeshContainerTests.Glb("""{"images":[{"uri":"../textures/rust.png"}]}"""));

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Warning);
        await Assert.That(findings[0].Message).Contains("no identity recorded");
    }

    [Test]
    public async Task a_glb_image_whose_uri_names_nothing_is_an_error()
    {
        using var fileSystem = CreateProject();
        WriteCarried(fileSystem, "/game/assets/models/crate.glb", "");
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", MeshContainerTests.Glb("""{"images":[{"uri":"../textures/gone.png"}]}"""));

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(findings[0].Message).Contains("does not exist under assets/");
    }

    [Test]
    public async Task a_recorded_glb_image_whose_texture_moved_is_a_warning_naming_where_it_went()
    {
        using var fileSystem = CreateProject();
        WriteCarried(fileSystem, "/game/assets/textures/metal/rust.png", "png");
        var rust = SidecarMeta.Load(fileSystem, "/game/assets/textures/metal/rust.png.meta").Guid;
        WriteCarried(fileSystem, "/game/assets/models/crate.glb", "");
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", MeshContainerTests.Glb("""{"images":[{"uri":"../textures/rust.png"}]}"""));
        MeshReferencesTests.Record(fileSystem, "/game/assets/models/crate.glb", "images[0]", "../textures/rust.png", new Paradise.Authoring.AssetReference(rust, "textures/rust.png"));

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Warning);
        await Assert.That(findings[0].Message).Contains("textures/metal/rust.png");
    }

    [Test]
    public async Task a_recorded_glb_image_whose_guid_nobody_carries_is_an_error()
    {
        using var fileSystem = CreateProject();
        WriteCarried(fileSystem, "/game/assets/models/crate.glb", "");
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", MeshContainerTests.Glb("""{"images":[{"uri":"../textures/rust.png"}]}"""));
        MeshReferencesTests.Record(fileSystem, "/game/assets/models/crate.glb", "images[0]", "../textures/rust.png", new Paradise.Authoring.AssetReference(Guid.NewGuid(), "textures/rust.png"));

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(findings[0].Message).Contains("no asset under assets/ carries");
    }

    [Test]
    public async Task a_reference_into_an_asset_whose_own_sidecar_is_missing_is_reported_once()
    {
        // The asset has no identity to match, so the finding belongs to the asset — repeating it
        // against every reference into it buries the one line that says what to do.
        using var fileSystem = CreateProject();
        fileSystem.CreateDirectory("/game/assets/materials");
        fileSystem.WriteAllText("/game/assets/materials/rust.toml", "a = 1\n");
        WriteDocumentWith(fileSystem, "/game/assets/levels/district.prefab", new CanonicalTomlTable
        {
            {
                "Material",
                AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(Guid.NewGuid(), "materials/rust.toml"))
            },
        });

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Path).IsEqualTo(new UPath("/game/assets/materials/rust.toml"));
        await Assert.That(findings[0].Message).Contains("no sidecar");
    }

    [Test]
    public async Task a_file_no_step_will_build_is_not_a_finding_on_its_own()
    {
        // Verify cannot tell, so verify does not say: an importer claims an asset inside its own
        // Import, on whatever grounds it likes, so only a running build can answer "does
        // anything handle this" — and even there a decline may mean "not for this tree".
        using var fileSystem = CreateProject();
        WriteCarried(fileSystem, "/game/assets/notes.txt", "todo");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings).IsEmpty();
    }

    [Test]
    public async Task a_file_nothing_builds_still_needs_its_identity()
    {
        using var fileSystem = CreateProject();
        fileSystem.WriteAllText("/game/assets/notes.txt", "todo");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(findings[0].Message).Contains("no sidecar");
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
    public async Task a_sidecar_naming_an_importer_the_chain_lacks_is_an_error()
    {
        using var fileSystem = CreateProject();
        AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        var meta = SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta");
        meta.Importer = "mine";
        meta.Save(fileSystem, "/game/assets/models/crate.glb.meta");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(findings[0].Message).Contains("names importer 'mine'");
    }

    [Test]
    public async Task a_sidecar_naming_no_importer_is_a_warning_that_fix_records()
    {
        using var fileSystem = CreateProject();
        fileSystem.CreateDirectory("/game/assets/models");
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", [1]);
        new SidecarMeta(Guid.NewGuid()).Save(fileSystem, "/game/assets/models/crate.glb.meta");

        var before = ProjectVerifier.Verify(fileSystem, s_layout);
        await Assert.That(before.Count).IsEqualTo(1);
        await Assert.That(before[0].Severity).IsEqualTo(VerifySeverity.Warning);
        await Assert.That(before[0].Message).Contains("'mesh' claims it");

        var repaired = ReferenceRepair.Fix(fileSystem, s_layout);

        await Assert.That(repaired.Select(r => r.Path)).Contains(new UPath("/game/assets/models/crate.glb.meta"));
        await Assert.That(SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta").Importer).IsEqualTo("mesh");
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
    }

    [Test]
    public async Task a_material_sampling_a_texture_nobody_carries_is_an_error_and_a_broken_one_names_the_line()
    {
        using var fileSystem = CreateProject();
        WriteDocument(fileSystem, "/game/assets/materials/grass.material",
            "BaseColorTexture = { guid = \"11111111-2222-4333-8444-555555555555\", path = \"textures/grass.png\" }\n");
        WriteDocument(fileSystem, "/game/assets/materials/broken.material", "BaseColorTexture = \"textures/grass.png\"\n");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(2);
        await Assert.That(findings.All(f => f.Severity == VerifySeverity.Error)).IsTrue();
        await Assert.That(findings.Any(f => f.Path.FullName.EndsWith("broken.material") && f.Message.Contains("BaseColorTexture"))).IsTrue();
        await Assert.That(findings.Any(f => f.Path.FullName.EndsWith("grass.material") && f.Message.Contains("no asset under assets/ carries"))).IsTrue();
    }

    [Test]
    public async Task a_leftover_hash_is_an_error_naming_the_sidecar()
    {
        // The field is gone from the format; a sidecar from before that is refused rather than
        // silently rewritten, so a stale branch learns what to run.
        using var fileSystem = CreateProject();
        fileSystem.CreateDirectory("/game/assets/models");
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", [1, 2, 3]);
        fileSystem.WriteAllText(
            "/game/assets/models/crate.glb.meta",
            """
            schema_version = 1
            guid = "3e1c4f60-2f5d-4e7c-a081-9c0d1e2f3041"
            importer = "mesh"
            hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

            """);

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(findings[0].Message).Contains("hash");
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

        // A misspelled settings domain: a warning, and one whose path sorts LAST, so ordering
        // by severity is the only thing that can put it behind the error below.
        fileSystem.WriteAllBytes("/game/assets/textures/zz-fire.png", [1]);
        var meta = SidecarMeta.Mint();
        meta.Importer = "texture";
        meta.SetSetting("texure", new CanonicalTomlTable { { "preset", "normal" } });
        meta.Save(fileSystem, "/game/assets/textures/zz-fire.png.meta");

        fileSystem.WriteAllBytes("/game/assets/models/a.glb", [1]);

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(2);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(findings[1].Severity).IsEqualTo(VerifySeverity.Warning);
    }

    [Test]
    public async Task an_upper_case_prefab_suffix_is_still_a_document_to_verify()
    {
        using var fileSystem = CreateProject();
        WriteDocument(fileSystem, "/game/assets/levels/Shouty.PREFAB", "not toml at all [[[");

        var findings = ProjectVerifier.Verify(fileSystem, s_layout);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(findings[0].Message).Contains("not valid TOML");
    }

    /// <param name="documentFormat">
    /// The <c>dev</c> profile's <c>document_format</c>, or null to leave profiles undeclared (which
    /// means the default, TOML).
    /// </param>
    internal static MemoryFileSystem CreateProject(string? documentFormat = null, string[]? ignore = null)
    {
        var fileSystem = new MemoryFileSystem();
        // The standard subtrees, because Zio does not create parents on write and empty
        // directories produce no findings anyway.
        fileSystem.CreateDirectory("/game/assets/models");
        fileSystem.CreateDirectory("/game/assets/textures");
        fileSystem.CreateDirectory("/game/assets/levels");

        var profiles = documentFormat is null
            ? ""
            : $"\n[build.profiles.dev]\ndocument_format = \"{documentFormat}\"\n";
        var assets = ignore is null
            ? ""
            : $"\n[assets]\nignore = [{string.Join(", ", ignore.Select(p => $"\"{p}\""))}]\n";

        // The manifest is an asset like everything else under assets/, so it carries an identity
        // too -- the only thing that does not is a sidecar, because one describing a sidecar is
        // an infinite regress.
        WriteDocument(fileSystem, "/game/assets/project.toml", $"name = \"shiningpie\"\nschema_version = 1\n{assets}{profiles}");
        return fileSystem;
    }

    /// <param name="importers">The chain the sidecar records its importer from; the built-ins when omitted. A test that builds with its own chain mints with it, as a watcher running that chain would have.</param>
    internal static void AddAssetWithSidecar(MemoryFileSystem fileSystem, UPath asset, IReadOnlyList<IAssetImporter>? importers = null)
    {
        fileSystem.CreateDirectory(asset.GetDirectory());
        fileSystem.WriteAllBytes(asset, [1, 2, 3]);
        Mint(fileSystem, asset, importers: importers);
    }

    internal static void WriteCanonicalDocument(MemoryFileSystem fileSystem, UPath path)
    {
        var document = new PrefabDocument();
        document.Objects.Add(PrefabObject.WithMeta(Guid.NewGuid(), "crate"));
        fileSystem.CreateDirectory(path.GetDirectory());
        PrefabDocumentSerializer.Save(fileSystem, path, document);
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

    internal static void MintDocumentSidecar(MemoryFileSystem fileSystem, UPath path) => Mint(fileSystem, path);

    /// <summary>A sidecar the way the maintainer mints one: identity plus the importer the built-in chain claims, so a fixture is what verify expects of a real tree.</summary>
    internal static Guid Mint(MemoryFileSystem fileSystem, UPath asset, Guid? guid = null, IReadOnlyList<IAssetImporter>? importers = null)
    {
        var meta = guid is { } identity ? new SidecarMeta(identity) : SidecarMeta.Mint();
        meta.Importer = ImporterChain.Claim(importers ?? AssetImporters.All, new ImportCandidate(fileSystem, s_layout, asset, null))?.Name;
        meta.Save(fileSystem, SidecarMeta.PathFor(asset));
        return meta.Guid;
    }

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
        Mint(fileSystem, path);
    }
}
