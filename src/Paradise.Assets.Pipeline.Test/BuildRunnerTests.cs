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
        public bool? LastFast;

        public string Identity => "fake-ktx 1.0";

        public bool TryEncode(byte[] source, string sourceExtension, TexturePreset preset, bool fastEncode, out byte[] ktx2, out string error)
        {
            Encodes++;
            LastFast = fastEncode;
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

        var result = new BuildRunner(fileSystem, s_layout, encoder).Run("dev");

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

        await Assert.That(runner.Run("dev").Succeeded).IsTrue();
        fileSystem.DeleteDirectory("/game/build", isRecursive: true);
        await Assert.That(runner.Run("dev").Succeeded).IsTrue();

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
        await Assert.That(encoder.LastFast).IsTrue();
        var built = Encoding.UTF8.GetString(fileSystem.ReadAllBytes("/game/build/textures/fire.ktx2"));
        await Assert.That(built).Contains(":Normal:");
    }

    [Test]
    public async Task audio_copies_through_byte_identical()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.CreateDirectory("/game/assets/audio");
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/init.bnk");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(fileSystem.ReadAllBytes("/game/build/audio/init.bnk"))
            .IsEquivalentTo(fileSystem.ReadAllBytes("/game/assets/audio/init.bnk"));
    }

    [Test]
    public async Task a_glb_without_embedded_images_copies_through()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb")).IsTrue();
    }

    [Test]
    public async Task a_glb_with_embedded_png_refuses_the_build()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var glb = MakeGlb("{\"images\":[{\"mimeType\":\"image/png\",\"bufferView\":0}]}");
        fileSystem.WriteAllBytes("/game/assets/models/bad.glb", glb);
        SidecarMeta.Mint().Save(fileSystem, "/game/assets/models/bad.glb.meta");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev");

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("embedded image/png");
    }

    [Test]
    public async Task a_mesh_texture_reference_is_repointed_at_the_built_ktx2()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/rust.png");
        var glb = MakeGlb("""{"images":[{"uri":"../textures/rust.png","mimeType":"image/png"}]}""");
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", glb);
        SidecarMeta.Mint().Save(fileSystem, "/game/assets/models/crate.glb.meta");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev");

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

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev");

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("../textures/gone.png");
        await Assert.That(result.Errors[0]).Contains("models/crate.glb");
    }

    [Test]
    public async Task config_documents_are_emitted_canonically_for_toml_profiles()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.WriteDocument(
            fileSystem, "/game/assets/config/game.toml", "# authored comment\nb = 2\na = 1\n");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev");

        await Assert.That(result.Succeeded).IsTrue();
        // Values verbatim, formatting canonical, comments dropped — build output is derived.
        await Assert.That(fileSystem.ReadAllText("/game/build/config/game.toml")).IsEqualTo("b = 2\na = 1\n");
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
        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev");

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

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev");

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("no sidecar");
    }

    [Test]
    public async Task a_missing_encoder_fails_only_when_textures_exist()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/audio/init.bnk");

        await Assert.That(new BuildRunner(fileSystem, s_layout, encoder: null).Run("dev").Succeeded).IsTrue();

        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");
        var result = new BuildRunner(fileSystem, s_layout, encoder: null).Run("dev");

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Errors[0]).Contains("no ktx CLI");
    }

    [Test]
    public async Task an_encode_failure_names_the_texture()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder { Fail = true }).Run("dev");

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

    [Test]
    public async Task the_play_target_writes_the_editor_tree_in_the_same_shape()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");

        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev", ProjectOutputTarget.Play);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/.editor/play/textures/fire.ktx2")).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/.editor/play/manifest.json")).IsTrue();
        await Assert.That(fileSystem.DirectoryExists("/game/build")).IsFalse();
    }

    [Test]
    public async Task no_manifest_is_written_on_a_failed_build()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/textures/fire.png");

        // A failing encode, because a document no longer fails a TOML build -- it builds one.
        var result = new BuildRunner(fileSystem, s_layout, new FakeEncoder { Fail = true }).Run("dev");

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(fileSystem.FileExists("/game/build/manifest.json")).IsFalse();
    }

    // ---- sidecars in the editor tree ----------------------------------------------------

    [Test]
    public async Task an_editor_build_carries_the_sidecars_and_a_shipped_build_does_not()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");

        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev", ProjectOutputTarget.Play);
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev", ProjectOutputTarget.Build);

        // The editor traces a built asset back to the document that produced it; a player's
        // install has no use for authoring identity and must not ship it.
        await Assert.That(fileSystem.FileExists("/game/.editor/play/models/crate.glb.meta")).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb.meta")).IsFalse();
    }

    // ---- the build index ----------------------------------------------------------------

    [Test]
    public async Task an_unchanged_source_is_not_rebuilt()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");

        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev", ProjectOutputTarget.Play);
        fileSystem.WriteAllBytes("/game/.editor/play/models/crate.glb", [9, 9, 9]);

        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev", ProjectOutputTarget.Play);

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
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev", ProjectOutputTarget.Play);

        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", [4, 5, 6]);
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev", ProjectOutputTarget.Play);

        await Assert.That(fileSystem.ReadAllBytes("/game/.editor/play/models/crate.glb")).IsEquivalentTo(new byte[] { 4, 5, 6 });
    }

    [Test]
    public async Task a_changed_sidecar_rebuilds_the_asset_it_describes()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev", ProjectOutputTarget.Play);
        fileSystem.WriteAllBytes("/game/.editor/play/models/crate.glb", [9, 9, 9]);

        // A new sidecar means a new GUID, which lands in the manifest -- so the asset's output
        // changed even though not one of its own bytes did. A key blind to this would keep
        // serving the old identity.
        SidecarMeta.Mint().Save(fileSystem, "/game/assets/models/crate.glb.meta");
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev", ProjectOutputTarget.Play);

        await Assert.That(fileSystem.ReadAllBytes("/game/.editor/play/models/crate.glb")).IsEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task a_deleted_output_is_rebuilt_even_though_the_source_is_unchanged()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev", ProjectOutputTarget.Play);

        fileSystem.DeleteFile("/game/.editor/play/models/crate.glb");
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev", ProjectOutputTarget.Play);

        await Assert.That(fileSystem.FileExists("/game/.editor/play/models/crate.glb")).IsTrue();
    }

    [Test]
    public async Task an_index_from_another_target_is_not_trusted()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev", ProjectOutputTarget.Play);

        // Hand the BUILD tree the PLAY tree's index. One source compiles to different artifacts
        // per profile and target, so reusing across them is the silently-wrong-artifact failure.
        fileSystem.CreateDirectory("/game/build");
        fileSystem.CopyFile("/game/.editor/play/.build-index.json", "/game/build/.build-index.json", overwrite: true);

        new BuildRunner(fileSystem, s_layout, new FakeEncoder()).Run("dev", ProjectOutputTarget.Build);

        await Assert.That(fileSystem.FileExists("/game/build/models/crate.glb")).IsTrue();
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
