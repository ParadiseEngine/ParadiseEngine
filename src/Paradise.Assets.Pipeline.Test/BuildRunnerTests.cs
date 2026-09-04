using System.Text;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;

namespace Paradise.Assets.Pipeline.Test;

public class BuildRunnerTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    private sealed class FakeEncoder : ITextureEncoder
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
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/init.bnk");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder(), importers: [new TwinOutputImporter()]).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("audio/init.bnk");
        await Assert.That(result.Errors[0]).Contains("audio/init.bank");
        await Assert.That(fileSystem.FileExists("/game/build/manifest.json")).IsFalse();
    }

    /// <summary>Writes the source at two extensions, both "primary" by stem, so one guid would name two files.</summary>
    private sealed class TwinOutputImporter : IAssetImporter
    {
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
    public async Task a_glb_without_embedded_images_copies_through()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb")).IsTrue();
    }

    [Test]
    public async Task a_glb_with_embedded_png_is_externalized_beside_the_mesh_through_the_cache()
    {
        // The rewriter is bytes-in/bytes-out, so the build can run it over the Zio mount (#212);
        // the encode goes through the same cache as an authored texture.
        using var fileSystem = ProjectVerifierTests.CreateProject();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4];
        fileSystem.WriteAllBytes("/game/assets/models/lamp.glb", EmbeddedImageGlb(png, "Lamp_Albedo"));
        SidecarMeta.Mint().Save(fileSystem, "/game/assets/models/lamp.glb.meta");
        var encoder = new FakeEncoder();
        var runner = new BuildRunner(fileSystem, s_layout, encoder);

        var result = runner.Run();

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(fileSystem.FileExists("/game/build/models/lamp_0.ktx2")).IsTrue();
        var manifest = BuildManifest.Load(fileSystem, "/game/build/manifest.json");
        await Assert.That(manifest.Assets.Select(a => a.Path)).IsEquivalentTo(new[] { "models/lamp.glb", "models/lamp_0.ktx2" });
        // The mesh's identity names the mesh; its externalised texture is a companion with none,
        // or byGuid would resolve the mesh reference to a texture.
        var guid = DocumentGuid.Format(SidecarMeta.Load(fileSystem, "/game/assets/models/lamp.glb.meta").Guid);
        await Assert.That(manifest.ByGuid[guid].Path).IsEqualTo("models/lamp.glb");
        await Assert.That(manifest.Assets.Single(a => a.Path == "models/lamp_0.ktx2").Guid).IsNull();

        GlbBinary.TryRead(fileSystem.ReadAllBytes("/game/build/models/lamp.glb"), out var gltf, out var bin);
        var image = gltf["images"]![0]!;
        await Assert.That(image["uri"]!.GetValue<string>()).IsEqualTo("lamp_0.ktx2");
        await Assert.That(image["mimeType"]!.GetValue<string>()).IsEqualTo("image/ktx2");
        await Assert.That(image["bufferView"]).IsNull();
        // Geometry only in the BIN: the image bytes are gone and the buffer says so.
        await Assert.That(bin.Length).IsLessThan(png.Length);
        await Assert.That(gltf["buffers"]![0]!["byteLength"]!.GetValue<int>()).IsEqualTo(bin.Length);
        // Same contract as a repointed texture reference (#207): declared through KHR_texture_basisu.
        await Assert.That(gltf["textures"]![0]!["extensions"]!["KHR_texture_basisu"]!["source"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(gltf["extensionsRequired"]!.AsArray().Count).IsEqualTo(1);

        fileSystem.DeleteDirectory("/game/build", isRecursive: true);
        await Assert.That(runner.Run().Succeeded).IsTrue();
        await Assert.That(encoder.Encodes).IsEqualTo(1);
    }

    [Test]
    public async Task an_embedded_png_without_an_encoder_names_the_mesh()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.WriteAllBytes("/game/assets/models/lamp.glb", EmbeddedImageGlb([1, 2, 3, 4], "Lamp_Albedo"));
        SidecarMeta.Mint().Save(fileSystem, "/game/assets/models/lamp.glb.meta");

        var result = new BuildRunner(fileSystem, s_layout, encoder: null).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("models/lamp.glb");
        await Assert.That(result.Errors[0]).Contains("no ktx CLI");
        await Assert.That(fileSystem.FileExists("/game/build/models/lamp.glb")).IsFalse();
    }

    [Test]
    public async Task a_mesh_texture_reference_is_repointed_at_the_built_ktx2()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/rust.png");
        var glb = MakeGlb("""{"images":[{"uri":"../textures/rust.png","mimeType":"image/png"}]}""");
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", glb);
        SidecarMeta.Mint().Save(fileSystem, "/game/assets/models/crate.glb.meta");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsTrue();
        var built = fileSystem.ReadAllBytes("/game/build/models/crate.glb");
        GlbBinary.TryRead(built, out var gltf, out _);
        await Assert.That(gltf["images"]![0]!["uri"]!.GetValue<string>()).IsEqualTo("../textures/rust.ktx2");
        // …and the file it now names was actually produced, at that relative place.
        await Assert.That(fileSystem.FileExists("/game/build/textures/rust.ktx2")).IsTrue();
    }

    [Test]
    public async Task a_mesh_referencing_a_texture_that_is_not_there_fails_the_build()
    {
        // The guard that stops the old failure mode: a mesh shipped pointing at a texture nothing
        // wrote, discovered as a blank surface in a renderer log rather than at the build.
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var glb = MakeGlb("""{"images":[{"uri":"../textures/gone.png","mimeType":"image/png"}]}""");
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", glb);
        SidecarMeta.Mint().Save(fileSystem, "/game/assets/models/crate.glb.meta");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("../textures/gone.png");
        await Assert.That(result.Errors[0]).Contains("models/crate.glb");

        // A failure writes nothing: the rewritten GLB must not be sitting in the output tree
        // pointing at a KTX2 the build never produced, which is the failure mode this guards.
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
        var level = Paradise.Export.Serialization.ExportTomlReader.ReadLevel(
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

        var level = Paradise.Export.Serialization.ExportTomlReader.ReadLevel(
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
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        var mine = new StubImporter("mine", ".glb", handles: true);

        // The whole reason the chain is walked backwards: a project extends the pipeline by
        // APPENDING, and what it appends has to win against the built-in it is replacing.
        var result = new BuildRunner(
            fileSystem, s_layout, new FakeEncoder(), importers: [.. AssetImporters.All, mine]).Run();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb.mine")).IsTrue();
        // MeshImporter never got the offer -- the chain stopped at the first claim, so the
        // built-in's copy is simply absent rather than written alongside.
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb")).IsFalse();
    }

    [Test]
    public async Task an_importer_that_declines_passes_the_asset_down_the_chain()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        var passive = new StubImporter("passive", ".glb", handles: false);

        var result = new BuildRunner(
            fileSystem, s_layout, new FakeEncoder(), importers: [.. AssetImporters.All, passive]).Run();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(passive.Offers).IsEqualTo(1);
        // Declining is not handling: MeshImporter, further down, still built the asset.
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb")).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb.passive")).IsFalse();
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
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        var guid = DocumentGuid.Format(SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta").Guid);

        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Build);

        await Assert.That(fileSystem.FileExists("/game/.editor/play/models/crate.glb.meta")).IsFalse();
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb.meta")).IsFalse();

        var play = BuildManifest.Load(fileSystem, "/game/.editor/play/manifest.json");
        var recorded = play.FindByGuid(guid);
        await Assert.That(recorded).IsNotNull();
        await Assert.That(recorded!.Path).IsEqualTo("models/crate.glb");
        await Assert.That(recorded.Source).IsEqualTo("models/crate.glb");
        await Assert.That(recorded.Sha256).IsNotEmpty();
        await Assert.That(recorded.Size).IsEqualTo(3);
        await Assert.That(play.FindByGuid(guid)!.Guid).IsEqualTo(guid);

        await Assert.That(play.ByGuid.ContainsKey(guid)).IsTrue();
        await Assert.That(play.ByGuid[guid].Path).IsEqualTo("models/crate.glb");
        await Assert.That(play.ByGuid[guid].Sha256).IsEqualTo(recorded.Sha256);
        await Assert.That(fileSystem.ReadAllText("/game/.editor/play/manifest.json"))
            .Contains($"\"{guid}\":");
    }

    // ---- the build index ----------------------------------------------------------------

    [Test]
    public async Task an_unchanged_source_is_not_rebuilt()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");

        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);
        fileSystem.WriteAllBytes("/game/.editor/play/models/crate.glb", [9, 9, 9]);

        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);

        // The marker survives, which is the only way to SEE a skip: the copy was not redone.
        await Assert.That(fileSystem.ReadAllBytes("/game/.editor/play/models/crate.glb")).IsEquivalentTo(new byte[] { 9, 9, 9 });
        // ...and the manifest still describes it. A skip that dropped the entry would leave the
        // manifest listing only what CHANGED, which is not what a manifest is.
        await Assert.That(fileSystem.ReadAllText("/game/.editor/play/manifest.json"))
            .Contains("\"path\": \"models/crate.glb\"");
    }

    [Test]
    public async Task a_changed_source_is_rebuilt()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);

        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", [4, 5, 6]);
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);

        await Assert.That(fileSystem.ReadAllBytes("/game/.editor/play/models/crate.glb")).IsEquivalentTo(new byte[] { 4, 5, 6 });
    }

    [Test]
    public async Task a_changed_sidecar_rebuilds_the_asset_it_describes()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);
        fileSystem.WriteAllBytes("/game/.editor/play/models/crate.glb", [9, 9, 9]);

        // A new sidecar means a new GUID, which lands in the manifest -- so the asset's output
        // changed even though not one of its own bytes did. A key blind to this would keep
        // serving the old identity.
        SidecarMeta.Mint().Save(fileSystem, "/game/assets/models/crate.glb.meta");
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);

        await Assert.That(fileSystem.ReadAllBytes("/game/.editor/play/models/crate.glb")).IsEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task a_deleted_output_is_rebuilt_even_though_the_source_is_unchanged()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);

        fileSystem.DeleteFile("/game/.editor/play/models/crate.glb");
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);

        await Assert.That(fileSystem.FileExists("/game/.editor/play/models/crate.glb")).IsTrue();
    }

    [Test]
    public async Task an_index_from_another_target_is_not_trusted()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Play);

        // Hand the BUILD tree the PLAY tree's index. One source compiles to different artifacts
        // per profile and target, so reusing across them is the silently-wrong-artifact failure.
        fileSystem.CreateDirectory("/game/build");
        fileSystem.CopyFile("/game/.editor/play/.build-index.json", "/game/build/.build-index.json", overwrite: true);

        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run(null, ProjectOutputTarget.Build);

        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb")).IsTrue();
    }

    // ---- the index tracks every input, not a flag (#201) ---------------------------------

    /// <summary>Incremental and clean builds must agree: a mesh whose texture vanished is an error either way, not a reused stale copy.</summary>
    [Test]
    public async Task a_mesh_is_rebuilt_when_the_texture_it_references_disappears()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/rust.png");
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", MakeGlb("""{"images":[{"uri":"../textures/rust.png","mimeType":"image/png"}]}"""));
        SidecarMeta.Mint().Save(fileSystem, "/game/assets/models/crate.glb.meta");
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Succeeded).IsTrue();

        fileSystem.DeleteFile("/game/assets/textures/rust.png");
        fileSystem.DeleteFile("/game/assets/textures/rust.png.meta");
        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("models/crate.glb");
        await Assert.That(result.Errors[0]).Contains("../textures/rust.png");
    }

    [Test]
    public async Task a_texture_appearing_rebuilds_the_mesh_that_was_waiting_for_it()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", MakeGlb("""{"images":[{"uri":"../textures/rust.png","mimeType":"image/png"}]}"""));
        SidecarMeta.Mint().Save(fileSystem, "/game/assets/models/crate.glb.meta");
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Succeeded).IsFalse();

        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/rust.png");
        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb")).IsTrue();
    }

    /// <summary>A prefab bakes the prefabs it instances: an edit to the instanced one changes this one's output, though not one of its own bytes did.</summary>
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
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/init.bnk");
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Succeeded).IsTrue();

        fileSystem.DeleteFile("/game/assets/models/crate.glb");
        fileSystem.DeleteFile("/game/assets/models/crate.glb.meta");
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Succeeded).IsTrue();

        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb")).IsFalse();
        await Assert.That(fileSystem.DirectoryExists("/game/build/models")).IsFalse();
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
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
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
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Succeeded).IsTrue();

        fileSystem.WriteAllBytes("/game/build/models/crate.glb", [1]);
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Succeeded).IsTrue();

        await Assert.That(fileSystem.ReadAllBytes("/game/build/models/crate.glb")).IsEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task a_failed_build_leaves_no_manifest_from_the_previous_one()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run().Succeeded).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/manifest.json")).IsTrue();

        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");
        await Assert.That(new BuildRunner(fileSystem, s_layout, new FakeEncoder { Fail = true }).Run().Succeeded).IsFalse();

        // The tree now holds outputs the old manifest never described; no manifest is the
        // honest state, and the next successful build writes a true one.
        await Assert.That(fileSystem.FileExists("/game/build/manifest.json")).IsFalse();
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb")).IsTrue();
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
        SidecarMeta.Mint().Save(fileSystem, "/game/assets/models/crate.glb.meta");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("'textures/Rust.png' does");
        await Assert.That(result.Errors[0]).Contains("case-exact");
    }

    [Test]
    public async Task a_reference_escaping_assets_is_refused()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.CreateDirectory("/game/textures");
        fileSystem.WriteAllBytes("/game/textures/rust.png", [1]);
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", MakeGlb("""{"images":[{"uri":"../../textures/rust.png","mimeType":"image/png"}]}"""));
        SidecarMeta.Mint().Save(fileSystem, "/game/assets/models/crate.glb.meta");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("outside assets/");
    }

    // ---- the build survives its importers (#203) -----------------------------------------

    private sealed class ThrowingImporter : IAssetImporter
    {
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
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
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
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        var writer = new SourceWritingImporter();

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder(), importers: [.. AssetImporters.All, writer]).Run();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("read-only");
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb.side")).IsFalse();
    }

    private sealed class SourceWritingImporter : IAssetImporter
    {
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
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/init.bnk");
        IReadOnlyList<IAssetImporter> chain = [.. AssetImporters.All, new OptionalCompanionImporter()];
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
