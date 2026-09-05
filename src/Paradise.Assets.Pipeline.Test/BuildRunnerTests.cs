using System.Text;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;

namespace Paradise.Assets.Pipeline.Test;

public class BuildRunnerTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    internal sealed class FakeEncoder : ITextureEncoder
    {
        public int Encodes;
        public bool Fail;
        public TextureQuality? LastQuality;

        public string Identity { get; set; } = "fake-ktx 1.0";

        public string CacheKey(byte[] source, string sourceExtension, TexturePreset preset, TextureQuality quality)
            => ArtifactDigest.Compute(source, sourceExtension, preset.ToString(), quality.ToString(), Identity);

        public bool TryEncode(byte[] source, string sourceExtension, TexturePreset preset, TextureQuality quality, out byte[] ktx2, out string error)
        {
            Encodes++;
            LastQuality = quality;
            if (Fail)
            {
                ktx2 = [];
                error = "injected encode failure";
                return false;
            }

            ktx2 = Encoding.UTF8.GetBytes($"ktx2:{preset}:{Convert.ToHexStringLower(source)}");
            error = "";
            return true;
        }
    }

    [Test]
    public async Task a_texture_project_builds_and_records_a_manifest()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");
        var encoder = new FakeEncoder();

        var result = new BuildRunner(fileSystem, s_layout, encoder).Run();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.AssetCount).IsEqualTo(1);
        await Assert.That(fileSystem.FileExists("/game/build/textures/fire.ktx2")).IsTrue();
        var manifest = fileSystem.ReadAllText("/game/build/manifest.json");
        await Assert.That(manifest).Contains("\"path\": \"textures/fire.ktx2\"");
        await Assert.That(manifest).Contains("\"source\": \"textures/fire.png\"");
        await Assert.That(manifest).Contains("\"guid\": ");
    }

    [Test]
    public async Task the_second_build_serves_textures_from_the_cache()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");
        var encoder = new FakeEncoder();
        var runner = new BuildRunner(fileSystem, s_layout, encoder);

        await Assert.That(runner.Run().Succeeded).IsTrue();
        fileSystem.DeleteDirectory("/game/build", isRecursive: true);
        await Assert.That(runner.Run().Succeeded).IsTrue();

        // One encode total: the rebuild after deleting build/ came from .editor/cache.
        await Assert.That(encoder.Encodes).IsEqualTo(1);
        await Assert.That(fileSystem.FileExists("/game/build/textures/fire.ktx2")).IsTrue();
    }

    [Test]
    public async Task the_sidecar_preset_and_the_profile_quality_reach_the_encoder()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.WriteAllText(
            "/game/assets/project.toml",
            "name = \"x\"\nschema_version = 1\n\n[build.profiles.fastdev]\ntexture_quality = \"fast\"\n");
        fileSystem.WriteAllBytes("/game/assets/textures/fire.png", [1]);
        var meta = SidecarMeta.Mint();
        meta.Importer = "texture";
        meta.SetSetting(TextureImportSettings.Domain, new CanonicalTomlTable { { "preset", "normal" } });
        meta.Save(fileSystem, "/game/assets/textures/fire.png.meta");
        var encoder = new FakeEncoder();

        var result = new BuildRunner(fileSystem, s_layout, encoder).Run("fastdev");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(encoder.LastQuality).IsEqualTo(TextureQuality.Fast);
        var built = Encoding.UTF8.GetString(fileSystem.ReadAllBytes("/game/build/textures/fire.ktx2"));
        await Assert.That(built).Contains(":Normal:");
    }

    [Test]
    public async Task two_primary_outputs_under_one_identity_fail_the_build_by_name()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        IReadOnlyList<IAssetImporter> chain = [new TwinOutputImporter()];
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/init.bnk", chain);

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder(), importers: chain).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("audio/init.bnk");
        await Assert.That(result.Errors[0]).Contains("audio/init.bank");
        await Assert.That(fileSystem.FileExists("/game/build/manifest.json")).IsFalse();
    }

    /// <summary>Writes the source at two extensions, both "primary" by stem, so one guid would name two files.</summary>
    private sealed class TwinOutputImporter : IAssetImporter
    {
        public bool Claims(ImportCandidate candidate) => candidate.HasExtension(".bnk");

        public string Name => "twin";

        public bool RecordsIdentity => true;

        public bool Import(ImportContext context, List<string> errors)
        {
            if (!context.HasExtension(".bnk")) return false;
            context.Output.WriteAllBytes("/" + context.Source, [1]);
            context.Output.WriteAllBytes("/" + Path.ChangeExtension(context.Source, ".bank"), [1]);
            return true;
        }
    }

    [Test]
    public async Task audio_copies_through_byte_identical()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.CreateDirectory("/game/assets/audio");
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/init.bnk");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(fileSystem.ReadAllBytes("/game/build/audio/init.bnk"))
            .IsEquivalentTo(fileSystem.ReadAllBytes("/game/assets/audio/init.bnk"));
    }

    [Test]
    public async Task a_glb_builds_nothing_its_extracted_assets_do()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsTrue();
        // Interchange, not a shipped asset: what the runtime draws is what `extract` made of it.
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb")).IsFalse();
    }

    [Test]
    public async Task a_json_gltf_is_refused_rather_than_copied_through_unrepointed()
    {
        // The importer claims .gltf but reads only the GLB container, so a JSON glTF reached
        // neither the rewrite nor the missing-texture check — it was copied through still naming
        // its .png, which is precisely the shipped-broken-mesh failure repointing exists to stop.
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.gltf");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("JSON glTF");
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.gltf")).IsFalse();
    }

    [Test]
    public async Task config_documents_are_emitted_canonically_for_toml_profiles()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteDocument(
            fileSystem, "/game/assets/config/game.toml", "# authored comment\nb = 2\na = 1\n");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsTrue();
        // Values verbatim, formatting canonical, comments dropped — build output is derived.
        await Assert.That(fileSystem.ReadAllText("/game/build/config/game.toml")).IsEqualTo("b = 2\na = 1\n");
    }

    /// <summary>inf is legal TOML with no JSON spelling: a json-profile build reports it against the file, not as an unhandled exception (issue #211).</summary>
    [Test]
    public async Task a_config_holding_inf_fails_a_json_build_with_a_named_error()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject(documentFormat: "json");
        ProjectVerifierTests.WriteDocument(fileSystem, "/game/assets/config/game.toml", "[limits]\nmax = inf\n");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev");

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("config/game.toml");
        await Assert.That(result.Errors[0]).Contains("limits.max");
        await Assert.That(fileSystem.FileExists("/game/build/config/game.json")).IsFalse();
    }

    [Test]
    public async Task a_document_bakes_to_the_export_contract()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject(documentFormat: "json");
        ProjectVerifierTests.WriteCanonicalDocument(fileSystem, "/game/assets/levels/district.prefab");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev");

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.Succeeded).IsTrue();

        // The extension changes because the built form is the contract, not the authoring source.
        // Since v6 the well-known payloads cross over untouched — the meta component, its type
        // string, and the authored identity all survive into the contract.
        var baked = fileSystem.ReadAllText("/game/build/levels/district.json");
        await Assert.That(baked).Contains("\"Entities\"");
        await Assert.That(baked).Contains("\"meta\"");
        await Assert.That(baked).Contains("\"SchemaVersion\": 6");
    }

    [Test]
    public async Task a_document_builds_to_toml_and_reads_back_as_the_contract()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteCanonicalDocument(fileSystem, "/game/assets/levels/district.prefab");

        // No document_format declared, so BuildProfile.Default applies -- and its default is TOML,
        // which used to be a promise the pipeline could not keep.
        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/levels/district.toml")).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/levels/district.json")).IsFalse();

        // Written as the contract, not as the authoring document: a baked level, readable by the
        // runtime's own reader.
        var level = Paradise.Export.Serialization.ExportTomlReader.ReadPrefab(
            fileSystem.ReadAllText("/game/build/levels/district.toml"));
        await Assert.That(level.Entities.Count).IsEqualTo(1);
    }

    [Test]
    public async Task the_profile_chooses_the_document_format()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject("json");
        ProjectVerifierTests.WriteCanonicalDocument(fileSystem, "/game/assets/levels/district.prefab");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/levels/district.json")).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/levels/district.toml")).IsFalse();
    }

    [Test]
    public async Task a_verify_error_refuses_the_build()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.WriteAllBytes("/game/assets/textures/fire.png", [1]); // no sidecar

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("no sidecar");
    }

    [Test]
    public async Task a_missing_encoder_fails_only_when_textures_exist()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/init.bnk");

        await Assert.That(new BuildRunner(fileSystem, s_layout, encoder: null).Run().Succeeded).IsTrue();

        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");
        var result = new BuildRunner(fileSystem, s_layout, encoder: null).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("no ktx CLI");
    }

    [Test]
    public async Task an_encode_failure_names_the_texture()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder { Fail = true }).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("textures/fire.png");
        await Assert.That(result.Errors[0]).Contains("injected encode failure");
    }

    [Test]
    public async Task an_unknown_profile_is_refused_naming_the_declared_ones()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("release");

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("no build profile 'release'");
    }

    /// <summary>
    /// No name is privileged, <c>dev</c> included.
    /// </summary>
    /// <remarks>
    /// The runner used to fall back to the defaults for an undeclared profile CALLED "dev",
    /// which made one English word mean something to a library that has no business having an
    /// opinion about it — and quietly diverged from whatever the CLI's <c>--profile</c> default
    /// happened to be, since the two hardcoded the string independently. Asking for a profile
    /// that does not exist is now an error whatever it is called; asking for none is null.
    /// </remarks>
    [Test]
    public async Task an_undeclared_profile_named_dev_is_refused_like_any_other()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev");

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("no build profile 'dev'");
    }

    [Test]
    public async Task naming_no_profile_builds_the_defaults_against_a_manifest_that_declares_none()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteCanonicalDocument(fileSystem, "/game/assets/levels/district.prefab");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        // BuildProfile.Default is TOML, so the document lands as .toml -- a project is buildable
        // before it has written a profiles table at all, which is what null is for.
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/levels/district.toml")).IsTrue();

        // Recorded as "", which no declared profile can be: the manifest reader refuses an empty
        // profile name, so an unnamed build never shares a build-index key with a named one.
        await Assert.That(fileSystem.ReadAllText("/game/build/manifest.json"))
            .Contains("\"profile\": \"\"");
    }

    [Test]
    public async Task the_play_target_writes_the_editor_tree_in_the_same_shape()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/.editor/play/textures/fire.ktx2")).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/.editor/play/manifest.json")).IsTrue();
        await Assert.That(fileSystem.DirectoryExists("/game/build")).IsFalse();
    }

    [Test]
    public async Task the_play_target_keeps_the_prefab_extension()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteCanonicalDocument(fileSystem, "/game/assets/levels/district.prefab");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/.editor/play/levels/district.prefab")).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/.editor/play/levels/district.toml")).IsFalse();
        await Assert.That(fileSystem.FileExists("/game/.editor/play/levels/district.json")).IsFalse();

        var level = Paradise.Export.Serialization.ExportTomlReader.ReadPrefab(
            fileSystem.ReadAllText("/game/.editor/play/levels/district.prefab"));
        await Assert.That(level.Entities.Count).IsEqualTo(1);
    }

    [Test]
    public async Task play_keeps_prefab_even_when_the_profile_is_json()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject("json");
        ProjectVerifierTests.WriteCanonicalDocument(fileSystem, "/game/assets/levels/district.prefab");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev", ProjectOutputTarget.Play);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/.editor/play/levels/district.prefab")).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/.editor/play/levels/district.json")).IsFalse();
    }

    [Test]
    public async Task no_manifest_is_written_on_a_failed_build()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");

        // A failing encode, because a document no longer fails a TOML build -- it builds one.
        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder { Fail = true }).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(fileSystem.FileExists("/game/build/manifest.json")).IsFalse();
    }

    // ---- the import chain ---------------------------------------------------------------

    /// <summary>An importer that answers whatever it is told, so a chain can be arranged.</summary>
    private sealed class StubImporter(string name, string extension, bool handles) : IAssetImporter
    {
        public bool Claims(ImportCandidate candidate) => handles && candidate.HasExtension(extension);

        public int Offers;

        public string Name => name;

        public bool RecordsIdentity => true;

        public bool Import(ImportContext context, List<string> errors)
        {
            if (!context.HasExtension(extension)) return false;

            Offers++;
            if (!handles) return false;

            context.Output.WriteAllBytes("/" + context.Source + $".{name}", [7]);
            return true;
        }
    }

    [Test]
    public async Task an_appended_importer_shadows_the_built_in_it_replaces()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var mine = new StubImporter("mine", ".glb", handles: true);
        IReadOnlyList<IAssetImporter> chain = [.. AssetImporters.All, mine];
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb", chain);

        // The whole reason the chain is walked backwards: a project extends the pipeline by
        // APPENDING, and what it appends has to win against the built-in it is replacing — for
        // an asset minted under that chain; one recorded earlier keeps its importer until edited.
        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder(), importers: chain).Run();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb.mine")).IsTrue();
        // MeshImporter never got the offer -- the chain stopped at the first claim, so the
        // built-in's copy is simply absent rather than written alongside.
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb")).IsFalse();
    }

    [Test]
    public async Task an_importer_that_does_not_claim_is_never_offered_and_the_claimant_builds()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        var passive = new StubImporter("passive", ".glb", handles: false);

        var result = new BuildRunner(
            fileSystem, s_layout, new FakeEncoder(), importers: [.. AssetImporters.All, passive]).Run();

        await Assert.That(result.Succeeded).IsTrue();
        // The sidecar names 'mesh'; a build does not search, so the appended non-claimant is never asked.
        await Assert.That(passive.Offers).IsEqualTo(0);
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb.passive")).IsFalse();
    }

    [Test]
    public async Task the_recorded_importer_is_used_without_searching_the_chain()
    {
        // A decoy appended LAST would win any claim; the sidecar names 'mesh', so it is never asked.
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        var decoy = new StubImporter("decoy", ".glb", handles: true);

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder(), importers: [.. AssetImporters.All, decoy]).Run();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(decoy.Offers).IsEqualTo(0);
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb.decoy")).IsFalse();
    }

    [Test]
    public async Task a_hand_picked_importer_is_honoured()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        var mine = new StubImporter("mine", ".glb", handles: true);
        var meta = SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta");
        meta.Importer = "mine";
        meta.Save(fileSystem, "/game/assets/models/crate.glb.meta");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder(), importers: [.. AssetImporters.All, mine]).Run();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(mine.Offers).IsEqualTo(1);
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb.mine")).IsTrue();
    }

    [Test]
    public async Task a_recorded_importer_the_chain_lacks_fails_the_build_naming_the_chain()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        var meta = SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta");
        meta.Importer = "mine";
        meta.Save(fileSystem, "/game/assets/models/crate.glb.meta");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("names importer 'mine'");
        await Assert.That(result.Errors[0]).Contains("mesh");
    }

    [Test]
    public async Task a_recorded_importer_that_declines_the_asset_is_an_error_not_a_skip()
    {
        // A hand edit that is wrong should be loud: a silent skip ships a tree missing the asset.
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        var meta = SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta");
        meta.Importer = "texture";
        meta.Save(fileSystem, "/game/assets/models/crate.glb.meta");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("names importer 'texture'");
        await Assert.That(result.Errors[0]).Contains("declined");
    }

    [Test]
    public async Task an_asset_nobody_claims_is_built_by_nobody()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/notes.txt");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        // Unclaimed is not an error -- verify already warned about it, and the build simply has
        // nothing to do. There is no classification gate saying so; the chain running out IS it.
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/notes.txt")).IsFalse();
    }

    [Test]
    public async Task the_manifest_configures_the_build_rather_than_being_built_by_it()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        // project.toml is a `.toml` the config importer must decline: compiling it would ship
        // the source project's profiles into the tree as if they were game data.
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/project.toml")).IsFalse();
    }

    // ---- identity in the built tree -----------------------------------------------------

    [Test]
    public async Task neither_tree_copies_source_sidecars_the_manifest_is_the_database()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/crate.bnk");
        var guid = DocumentGuid.Format(SidecarMeta.Load(fileSystem, "/game/assets/audio/crate.bnk.meta").Guid);

        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Build);

        await Assert.That(fileSystem.FileExists("/game/.editor/play/audio/crate.bnk.meta")).IsFalse();
        await Assert.That(fileSystem.FileExists("/game/build/audio/crate.bnk.meta")).IsFalse();

        var play = BuildManifest.Load(fileSystem, "/game/.editor/play/manifest.json");
        var recorded = play.FindByGuid(guid);
        await Assert.That(recorded).IsNotNull();
        await Assert.That(recorded!.Path).IsEqualTo("audio/crate.bnk");
        await Assert.That(recorded.Source).IsEqualTo("audio/crate.bnk");
        await Assert.That(recorded.Sha256).IsNotEmpty();
        await Assert.That(recorded.Size).IsEqualTo(3);
        await Assert.That(play.FindByGuid(guid)!.Guid).IsEqualTo(guid);

        await Assert.That(play.ByGuid.ContainsKey(guid)).IsTrue();
        await Assert.That(play.ByGuid[guid].Path).IsEqualTo("audio/crate.bnk");
        await Assert.That(play.ByGuid[guid].Sha256).IsEqualTo(recorded.Sha256);
        await Assert.That(fileSystem.ReadAllText("/game/.editor/play/manifest.json"))
            .Contains($"\"{guid}\":");
    }

    // ---- the build index ----------------------------------------------------------------

    [Test]
    public async Task an_unchanged_source_is_not_rebuilt()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/crate.bnk");

        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);
        fileSystem.WriteAllBytes("/game/.editor/play/audio/crate.bnk", [9, 9, 9]);

        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);

        // The marker survives, which is the only way to SEE a skip: the copy was not redone.
        await Assert.That(fileSystem.ReadAllBytes("/game/.editor/play/audio/crate.bnk")).IsEquivalentTo(new byte[] { 9, 9, 9 });
        // ...and the manifest still describes it. A skip that dropped the entry would leave the
        // manifest listing only what CHANGED, which is not what a manifest is.
        await Assert.That(fileSystem.ReadAllText("/game/.editor/play/manifest.json"))
            .Contains("\"path\": \"audio/crate.bnk\"");
    }

    [Test]
    public async Task a_changed_source_is_rebuilt()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/crate.bnk");
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);

        fileSystem.WriteAllBytes("/game/assets/audio/crate.bnk", [4, 5, 6]);
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);

        await Assert.That(fileSystem.ReadAllBytes("/game/.editor/play/audio/crate.bnk")).IsEquivalentTo(new byte[] { 4, 5, 6 });
    }

    [Test]
    public async Task a_changed_sidecar_rebuilds_the_asset_it_describes()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/crate.bnk");
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);
        fileSystem.WriteAllBytes("/game/.editor/play/audio/crate.bnk", [9, 9, 9]);

        // A new sidecar means a new GUID, which lands in the manifest -- so the asset's output
        // changed even though not one of its own bytes did. A key blind to this would keep
        // serving the old identity.
        ProjectVerifierTests.Mint(fileSystem, "/game/assets/audio/crate.bnk");
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);

        await Assert.That(fileSystem.ReadAllBytes("/game/.editor/play/audio/crate.bnk")).IsEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task a_deleted_output_is_rebuilt_even_though_the_source_is_unchanged()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/crate.bnk");
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);

        fileSystem.DeleteFile("/game/.editor/play/audio/crate.bnk");
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);

        await Assert.That(fileSystem.FileExists("/game/.editor/play/audio/crate.bnk")).IsTrue();
    }

    [Test]
    public async Task an_index_from_another_target_is_not_trusted()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/crate.bnk");
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);

        // Hand the BUILD tree the PLAY tree's index. One source compiles to different artifacts
        // per profile and target, so reusing across them is the silently-wrong-artifact failure.
        fileSystem.CreateDirectory("/game/build");
        fileSystem.CopyFile("/game/.editor/play/.build-index.json", "/game/build/.build-index.json", overwrite: true);

        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Build);

        await Assert.That(fileSystem.FileExists("/game/build/audio/crate.bnk")).IsTrue();
    }

    // ---- the index tracks every input, not a flag (#201) ---------------------------------

    /// <summary>Incremental and clean builds must agree: a mesh whose texture vanished is an error either way, not a reused stale copy.</summary>
    [Test]
    public async Task a_scene_is_rebuilt_when_a_prefab_it_instances_changes()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteCanonicalDocument(fileSystem, "/game/assets/prefabs/crate.prefab");
        var crateGuid = SidecarMeta.Load(fileSystem, "/game/assets/prefabs/crate.prefab.meta").Guid;
        var scene = new PrefabDocument();
        var instance = PrefabObject.WithMeta(Guid.NewGuid(), "crate_01");
        instance.Prefab = new Paradise.Authoring.AssetReference(crateGuid, "prefabs/crate.prefab");
        scene.Objects.Add(instance);
        PrefabDocumentSerializer.Save(fileSystem, "/game/assets/levels/scene.prefab", scene);
        ProjectVerifierTests.MintDocumentSidecar(fileSystem, "/game/assets/levels/scene.prefab");
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Errors).IsEmpty();
        var first = fileSystem.ReadAllText("/game/build/levels/scene.toml");

        // Untouched: the scene is served from the index, and the marker proves it.
        fileSystem.WriteAllText("/game/build/levels/scene.toml", new string('#', first.Length));
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Errors).IsEmpty();
        await Assert.That(fileSystem.ReadAllText("/game/build/levels/scene.toml")).IsEqualTo(new string('#', first.Length));

        var crate = PrefabDocumentSerializer.Load(fileSystem, "/game/assets/prefabs/crate.prefab");
        crate.Objects.Add(PrefabObject.WithMeta(Guid.NewGuid(), "lid", parent: crate.Objects[0].Guid));
        PrefabDocumentSerializer.Save(fileSystem, "/game/assets/prefabs/crate.prefab", crate);
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Errors).IsEmpty();

        var second = fileSystem.ReadAllText("/game/build/levels/scene.toml");
        await Assert.That(second).IsNotEqualTo(first);
        await Assert.That(second).Contains("lid");
    }

    /// <summary>A rename done outside `mv` — Finder, `git mv` — leaves every reference into the asset spelling the old path. The guid still names it, so the build must not care.</summary>
    [Test]
    public async Task a_prefab_instance_whose_path_a_rename_left_stale_still_builds()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteCanonicalDocument(fileSystem, "/game/assets/prefabs/barrel.prefab");
        var barrel = PrefabDocumentSerializer.Load(fileSystem, "/game/assets/prefabs/barrel.prefab");
        barrel.Objects.Add(PrefabObject.WithMeta(Guid.NewGuid(), "lid", parent: barrel.Objects[0].Guid));
        PrefabDocumentSerializer.Save(fileSystem, "/game/assets/prefabs/barrel.prefab", barrel);
        var guid = SidecarMeta.Load(fileSystem, "/game/assets/prefabs/barrel.prefab.meta").Guid;

        var scene = new PrefabDocument();
        var instance = PrefabObject.WithMeta(Guid.NewGuid(), "barrel_01");
        instance.Prefab = new Paradise.Authoring.AssetReference(guid, "prefabs/crate.prefab");
        scene.Objects.Add(instance);
        PrefabDocumentSerializer.Save(fileSystem, "/game/assets/levels/scene.prefab", scene);
        ProjectVerifierTests.MintDocumentSidecar(fileSystem, "/game/assets/levels/scene.prefab");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        // The child comes only from the prefab, so it is there exactly when the guid found it.
        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(fileSystem.ReadAllText("/game/build/levels/scene.toml")).Contains("lid");
    }

    /// <summary>The instance resolved by guid; the reference the bake FLATTENS must point at the built path of that same asset, not at the stale one nothing wrote.</summary>
    [Test]
    public async Task a_stale_reference_bakes_the_path_its_asset_actually_built_to()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteDocument(fileSystem, "/game/assets/materials/patina.toml", "a = 1\n");
        var guid = SidecarMeta.Load(fileSystem, "/game/assets/materials/patina.toml.meta").Guid;
        ProjectVerifierTests.WriteDocumentWith(fileSystem, "/game/assets/levels/scene.prefab", new CanonicalTomlTable
        {
            {
                "Material",
                AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(guid, "materials/rust.toml"))
            },
        });

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Errors).IsEmpty();
        var baked = fileSystem.ReadAllText("/game/build/levels/scene.toml");
        await Assert.That(baked).Contains("materials/patina.toml");
        await Assert.That(baked).DoesNotContain("materials/rust.toml");
    }

    /// <summary>A reference is flattened to the referenced asset's BUILT path, so where that asset lives is an input of this one — and a rename that touches not one byte of the referencing document must still rebuild it.</summary>
    [Test]
    public async Task moving_a_referenced_asset_rebuilds_the_document_that_references_it()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteDocument(fileSystem, "/game/assets/materials/rust.toml", "a = 1\n");
        var guid = SidecarMeta.Load(fileSystem, "/game/assets/materials/rust.toml.meta").Guid;
        ProjectVerifierTests.WriteDocumentWith(fileSystem, "/game/assets/levels/scene.prefab", new CanonicalTomlTable
        {
            {
                "Material",
                AssetReferenceCodec.Write(new Paradise.Authoring.AssetReference(guid, "materials/rust.toml"))
            },
        });
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Errors).IsEmpty();
        await Assert.That(fileSystem.ReadAllText("/game/build/levels/scene.toml")).Contains("materials/rust.toml");

        // A Finder rename: the file and its sidecar move, and no document is rewritten.
        fileSystem.MoveFile("/game/assets/materials/rust.toml", "/game/assets/materials/patina.toml");
        fileSystem.MoveFile("/game/assets/materials/rust.toml.meta", "/game/assets/materials/patina.toml.meta");

        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Errors).IsEmpty();
        await Assert.That(fileSystem.ReadAllText("/game/build/levels/scene.toml")).Contains("materials/patina.toml");
    }

    /// <summary>Textures used to opt out of the index and re-fetch from the cache every run; now an unchanged texture costs nothing, not even a cache lookup.</summary>
    [Test]
    public async Task an_unchanged_texture_is_served_by_the_index_without_the_cache()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");
        var encoder = new FakeEncoder();
        await Assert.That(new BuildRunner(fileSystem, s_layout, encoder).Run().Succeeded).IsTrue();

        fileSystem.DeleteDirectory("/game/.editor/cache", isRecursive: true);
        var built = fileSystem.ReadAllBytes("/game/build/textures/fire.ktx2");
        var marker = new byte[built.Length];
        fileSystem.WriteAllBytes("/game/build/textures/fire.ktx2", marker);

        await Assert.That(new BuildRunner(fileSystem, s_layout, encoder).Run().Succeeded).IsTrue();

        await Assert.That(encoder.Encodes).IsEqualTo(1);
        await Assert.That(fileSystem.ReadAllBytes("/game/build/textures/fire.ktx2")).IsEquivalentTo(marker);
    }

    [Test]
    public async Task a_different_encoder_rebuilds_every_texture()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");
        var encoder = new FakeEncoder();
        await Assert.That(new BuildRunner(fileSystem, s_layout, encoder).Run().Succeeded).IsTrue();

        encoder.Identity = "fake-ktx 2.0";
        await Assert.That(new BuildRunner(fileSystem, s_layout, encoder).Run().Succeeded).IsTrue();

        await Assert.That(encoder.Encodes).IsEqualTo(2);
    }

    /// <summary>The profile reaches importers as settings, not as a file they read, so the manifest is folded into the index environment.</summary>
    [Test]
    public async Task a_changed_profile_setting_rebuilds_what_depends_on_it()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.WriteAllText("/game/assets/project.toml", "name = \"x\"\nschema_version = 1\n\n[build.profiles.dev]\ntexture_quality = \"full\"\n");
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");
        var encoder = new FakeEncoder();
        await Assert.That(new BuildRunner(fileSystem, s_layout, encoder).Run("dev").Succeeded).IsTrue();
        await Assert.That(encoder.LastQuality).IsEqualTo(TextureQuality.Full);

        fileSystem.WriteAllText("/game/assets/project.toml", "name = \"x\"\nschema_version = 1\n\n[build.profiles.dev]\ntexture_quality = \"fast\"\n");
        await Assert.That(new BuildRunner(fileSystem, s_layout, encoder).Run("dev").Succeeded).IsTrue();

        await Assert.That(encoder.Encodes).IsEqualTo(2);
        await Assert.That(encoder.LastQuality).IsEqualTo(TextureQuality.Fast);
    }

    [Test]
    public async Task a_texture_served_from_the_cache_is_in_the_manifest()
    {
        // CopyFileCross unwraps a composed destination, so a cache hit used to land in the tree
        // without passing the recording mount — and the manifest lost the texture.
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");
        var encoder = new FakeEncoder();
        await Assert.That(new BuildRunner(fileSystem, s_layout, encoder).Run().Succeeded).IsTrue();
        fileSystem.DeleteDirectory("/game/build", isRecursive: true);

        await Assert.That(new BuildRunner(fileSystem, s_layout, encoder).Run().Succeeded).IsTrue();

        await Assert.That(encoder.Encodes).IsEqualTo(1);
        await Assert.That(fileSystem.ReadAllText("/game/build/manifest.json")).Contains("\"path\": \"textures/fire.ktx2\"");
    }

    // ---- the tree holds exactly what the build produced (#201, #202) ---------------------

    [Test]
    public async Task the_output_of_a_deleted_source_is_swept()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/crate.bnk");
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/init.bnk");
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Succeeded).IsTrue();

        fileSystem.DeleteFile("/game/assets/audio/crate.bnk");
        fileSystem.DeleteFile("/game/assets/audio/crate.bnk.meta");
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Succeeded).IsTrue();

        await Assert.That(fileSystem.FileExists("/game/build/audio/crate.bnk")).IsFalse();
        await Assert.That(fileSystem.FileExists("/game/build/audio/init.bnk")).IsTrue();
        await Assert.That(fileSystem.ReadAllText("/game/build/manifest.json")).DoesNotContain("crate.glb");
    }

    /// <summary>ShiningPie's play tree held both <c>box.prefab</c> and <c>box.toml</c> after the play-target extension policy changed; the runtime dispatches on extension.</summary>
    [Test]
    public async Task an_output_under_a_retired_naming_policy_is_swept()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteCanonicalDocument(fileSystem, "/game/assets/levels/box.prefab");
        fileSystem.CreateDirectory("/game/.editor/play/levels");
        fileSystem.CreateDirectory("/game/.editor/play/models");
        fileSystem.WriteAllText("/game/.editor/play/levels/box.toml", "stale");
        fileSystem.WriteAllBytes("/game/.editor/play/models/x.glb.0123.partial", [1]);

        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play).Succeeded).IsTrue();

        await Assert.That(fileSystem.FileExists("/game/.editor/play/levels/box.prefab")).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/.editor/play/levels/box.toml")).IsFalse();
        await Assert.That(fileSystem.FileExists("/game/.editor/play/models/x.glb.0123.partial")).IsFalse();
    }

    [Test]
    public async Task a_failed_build_does_not_sweep()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/crate.bnk");
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Succeeded).IsTrue();
        fileSystem.WriteAllText("/game/build/stray.txt", "left by an older policy");

        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder { Fail = true }).Run().Succeeded).IsFalse();

        // Nothing is decided about a tree the build did not finish.
        await Assert.That(fileSystem.FileExists("/game/build/stray.txt")).IsTrue();
    }

    [Test]
    public async Task a_truncated_output_is_rebuilt()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/crate.bnk");
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Succeeded).IsTrue();

        fileSystem.WriteAllBytes("/game/build/audio/crate.bnk", [1]);
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Succeeded).IsTrue();

        await Assert.That(fileSystem.ReadAllBytes("/game/build/audio/crate.bnk")).IsEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task a_failed_build_leaves_no_manifest_from_the_previous_one()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/crate.bnk");
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Succeeded).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/manifest.json")).IsTrue();

        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder { Fail = true }).Run().Succeeded).IsFalse();

        // The tree now holds outputs the old manifest never described; no manifest is the
        // honest state, and the next successful build writes a true one.
        await Assert.That(fileSystem.FileExists("/game/build/manifest.json")).IsFalse();
        await Assert.That(fileSystem.FileExists("/game/build/audio/crate.bnk")).IsTrue();
    }

    [Test]
    public async Task two_sources_landing_on_one_output_fail_naming_both()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.jpg");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors.Count).IsEqualTo(1);
        await Assert.That(result.Errors[0]).Contains("textures/fire.jpg");
        await Assert.That(result.Errors[0]).Contains("textures/fire.png");
        await Assert.That(result.Errors[0]).Contains("textures/fire.ktx2");
    }

    [Test]
    public async Task a_collision_with_a_reused_output_is_still_a_collision()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Succeeded).IsTrue();

        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.jpg");
        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("textures/fire.ktx2");
    }

    // ---- references are case-exact and stay inside assets/ (#202) ----------------------

    [Test]
    public async Task a_reference_with_the_wrong_case_is_refused_naming_the_real_file()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/Rust.png");
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", MakeGlb("""{"images":[{"uri":"../textures/rust.png","mimeType":"image/png"}]}"""));
        ProjectVerifierTests.Mint(fileSystem, "/game/assets/models/crate.glb");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("'textures/Rust.png' does");
        await Assert.That(result.Errors[0]).Contains("case-exact");
    }

    [Test]
    public async Task a_material_bakes_its_texture_slot_to_the_ktx2_where_the_texture_now_lives()
    {
        // The reference's path is stale (the texture moved in Finder); the guid resolves it, and
        // the baked material names the KTX2 the texture step writes at the texture's real place.
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.CreateDirectory("/game/assets/textures/ground");
        fileSystem.WriteAllBytes("/game/assets/textures/ground/grass.png", [1]);
        var grass = ProjectVerifierTests.Mint(fileSystem, "/game/assets/textures/ground/grass.png");
        ProjectVerifierTests.WriteDocument(fileSystem, "/game/assets/materials/grass.material",
            $"Name = \"grass\"\nBaseColorTexture = {{ guid = \"{grass}\", path = \"textures/grass.png\" }}\n");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(fileSystem.FileExists("/game/build/materials/grass.material")).IsTrue();
        await Assert.That(fileSystem.ReadAllText("/game/build/materials/grass.material")).Contains("BaseColorTexture = \"textures/ground/grass.ktx2\"");
    }

    [Test]
    public async Task a_material_naming_a_texture_nobody_carries_fails_the_build_by_name()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteDocument(fileSystem, "/game/assets/materials/grass.material",
            "BaseColorTexture = { guid = \"11111111-2222-4333-8444-555555555555\", path = \"textures/grass.png\" }\n");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors.Any(e => e.Contains("grass.material") && e.Contains("no asset under assets/ carries"))).IsTrue();
    }

    [Test]
    public async Task a_reference_escaping_assets_is_refused()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.CreateDirectory("/game/textures");
        fileSystem.WriteAllBytes("/game/textures/rust.png", [1]);
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", MakeGlb("""{"images":[{"uri":"../../textures/rust.png","mimeType":"image/png"}]}"""));
        ProjectVerifierTests.Mint(fileSystem, "/game/assets/models/crate.glb");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("outside assets/");
    }

    // ---- the build survives its importers (#203) -----------------------------------------

    private sealed class ThrowingImporter : IAssetImporter
    {
        public bool Claims(ImportCandidate candidate) => candidate.HasExtension(".glb");

        public string Name => "throwing";

        public bool RecordsIdentity => true;

        public bool Import(ImportContext context, List<string> errors)
        {
            if (!context.HasExtension(".glb")) return false;
            throw new IOException("the file is still being written");
        }
    }

    [Test]
    public async Task an_importer_that_throws_costs_one_asset_not_the_build()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb", [.. AssetImporters.All, new ThrowingImporter()]);
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/init.bnk");

        var result = new BuildRunner(
            fileSystem, s_layout, new FakeEncoder(), importers: [.. AssetImporters.All, new ThrowingImporter()]).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors.Count).IsEqualTo(1);
        await Assert.That(result.Errors[0]).Contains("models/crate.glb");
        await Assert.That(result.Errors[0]).Contains("IOException");
        await Assert.That(result.Errors[0]).Contains("still being written");
        await Assert.That(fileSystem.FileExists("/game/build/audio/init.bnk")).IsTrue();
    }

    [Test]
    public async Task an_importer_cannot_write_into_assets()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        
        var writer = new SourceWritingImporter();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb", [.. AssetImporters.All, writer]);

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder(), importers: [.. AssetImporters.All, writer]).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("read-only");
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb.side")).IsFalse();
    }

    private sealed class SourceWritingImporter : IAssetImporter
    {
        public bool Claims(ImportCandidate candidate) => candidate.HasExtension(".glb");

        public string Name => "writer";

        public bool RecordsIdentity => true;

        public bool Import(ImportContext context, List<string> errors)
        {
            if (!context.HasExtension(".glb")) return false;
            context.FileSystem.WriteAllBytes(context.Asset.FullName + ".side", [1]);
            return true;
        }
    }

    /// <summary>An importer reads a companion file that may not exist and swallows the miss; the miss is still an input.</summary>
    private sealed class OptionalCompanionImporter : IAssetImporter
    {
        public bool Claims(ImportCandidate candidate) => candidate.HasExtension(".bnk");

        public string Name => "companion";

        public bool RecordsIdentity => true;

        public bool Import(ImportContext context, List<string> errors)
        {
            if (!context.HasExtension(".bnk")) return false;

            byte[] companion;
            try
            {
                companion = context.FileSystem.ReadAllBytes(context.Asset.FullName + ".txt");
            }
            catch (FileNotFoundException)
            {
                companion = [];
            }

            context.Output.WriteAllBytes("/" + context.Source, [.. context.FileSystem.ReadAllBytes(context.Asset), .. companion]);
            return true;
        }
    }

    [Test]
    public async Task a_read_that_missed_is_recorded_so_the_file_appearing_rebuilds()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        IReadOnlyList<IAssetImporter> chain = [.. AssetImporters.All, new OptionalCompanionImporter()];
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/init.bnk", chain);
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder(), importers: chain).Run().Succeeded).IsTrue();
        await Assert.That(fileSystem.ReadAllBytes("/game/build/audio/init.bnk")).IsEquivalentTo(new byte[] { 1, 2, 3 });

        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/init.bnk.txt");
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder(), importers: chain).Run().Succeeded).IsTrue();

        await Assert.That(fileSystem.ReadAllBytes("/game/build/audio/init.bnk")).IsEquivalentTo(new byte[] { 1, 2, 3, 1, 2, 3 });
    }

    [Test]
    public async Task an_ignored_file_under_assets_is_neither_verified_nor_built()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject(ignore: [".DS_Store", "*.tmp"]);
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/init.bnk");
        fileSystem.WriteAllBytes("/game/assets/audio/.DS_Store", [0]);
        fileSystem.WriteAllBytes("/game/assets/audio/init.bnk.tmp", [0]);

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.AssetCount).IsEqualTo(1);
        await Assert.That(fileSystem.FileExists("/game/build/audio/.DS_Store")).IsFalse();
    }

    internal static byte[] EmbeddedImageGlb(byte[] image, string name)
    {
        var gltf = new System.Text.Json.Nodes.JsonObject
        {
            ["asset"] = new System.Text.Json.Nodes.JsonObject { ["version"] = "2.0" },
            ["images"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["name"] = name, ["mimeType"] = "image/png", ["bufferView"] = 0 }),
            ["textures"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["source"] = 0 }),
            ["bufferViews"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["buffer"] = 0, ["byteOffset"] = 0, ["byteLength"] = image.Length }),
            ["buffers"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["byteLength"] = image.Length }),
        };
        return GlbBinary.Write(gltf, image);
    }

    private static byte[] MakeGlb(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var padded = payload.Length % 4 == 0 ? payload : [.. payload, .. new byte[4 - payload.Length % 4].Select(_ => (byte)' ')];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46546C67u);
        writer.Write(2u);
        writer.Write(12 + 8 + padded.Length);
        writer.Write(padded.Length);
        writer.Write(0x4E4F534Au);
        writer.Write(padded);
        writer.Flush();
        return stream.ToArray();
    }
}
