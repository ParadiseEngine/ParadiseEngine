using Paradise.Assets.Documents;

namespace Paradise.Assets.Pipeline.Test;

public class AssetClassifierTests
{
    private static readonly UPath s_assets = "/game/assets";

    [Test]
    [Arguments("/game/assets/project.toml", AssetClass.Manifest)]
    [Arguments("/game/assets/levels/district.prefab", AssetClass.Prefab)]
    [Arguments("/game/assets/config/game.toml", AssetClass.Config)]
    [Arguments("/game/assets/models/crate.glb.meta", AssetClass.Sidecar)]
    [Arguments("/game/assets/models/crate.glb", AssetClass.Foreign)]
    [Arguments("/game/assets/models/crate.gltf", AssetClass.Foreign)]
    [Arguments("/game/assets/textures/fire.PNG", AssetClass.Foreign)]
    [Arguments("/game/assets/textures/fire.jpg", AssetClass.Foreign)]
    [Arguments("/game/assets/audio/init.bnk", AssetClass.Foreign)]
    [Arguments("/game/assets/audio/music.wem", AssetClass.Foreign)]
    [Arguments("/game/assets/notes.txt", AssetClass.Other)]
    [Arguments("/game/assets/other/project.toml", AssetClass.Config)]
    public async Task files_classify_by_extension_and_place(string path, AssetClass expected)
    {
        await Assert.That(AssetClassifier.Classify(s_assets, path)).IsEqualTo(expected);
    }

    [Test]
    public async Task a_scene_sidecar_would_be_a_sidecar_not_a_scene()
    {
        // ".prefab.meta" should never exist (scenes carry ids in-file), but if one
        // appears the sidecar rules see it — and verify reports it as orphan or mismatch.
        await Assert.That(AssetClassifier.Classify(s_assets, "/game/assets/levels/a.prefab.meta"))
            .IsEqualTo(AssetClass.Sidecar);
    }

    [Test]
    public async Task the_importer_claiming_a_path_follows_the_extension()
    {
        await Assert.That(AssetImporters.Find("/a/x.glb")).IsTypeOf<MeshImporter>();
        await Assert.That(AssetImporters.Find("/a/x.jpeg")).IsTypeOf<TextureImporter>();
        await Assert.That(AssetImporters.Find("/a/x.bnk")).IsTypeOf<AudioImporter>();
        await Assert.That(AssetImporters.Find("/a/x.txt")).IsNull();
    }
}
