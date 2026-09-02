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
    // A file nothing will ever build is Foreign too. The classifier reads paths, not importers,
    // so it cannot tell this from the mesh above -- and does not pretend to.
    [Arguments("/game/assets/notes.txt", AssetClass.Foreign)]
    [Arguments("/game/assets/other/project.toml", AssetClass.Config)]
    [Arguments("/game/assets/.DS_Store", AssetClass.Junk)]
    [Arguments("/game/assets/models/Thumbs.db", AssetClass.Junk)]
    [Arguments("/game/assets/models/desktop.ini", AssetClass.Junk)]
    [Arguments("/game/assets/models/crate.glb~", AssetClass.Junk)]
    [Arguments("/game/assets/models/crate.glb.tmp", AssetClass.Junk)]
    [Arguments("/game/assets/models/crate.glb.TMP", AssetClass.Junk)]
    [Arguments("/game/assets/models/.#crate.glb", AssetClass.Junk)]
    [Arguments("/game/assets/props/crate.blend1", AssetClass.Junk)]
    [Arguments("/game/assets/props/crate.blend32", AssetClass.Junk)]
    [Arguments("/game/assets/props/crate.blend", AssetClass.Foreign)]
    [Arguments("/game/assets/props/tmp.png", AssetClass.Foreign)]
    // The sidecar check wins over the junk check so that verify can see a sidecar minted for junk.
    [Arguments("/game/assets/.DS_Store.meta", AssetClass.Sidecar)]
    public async Task files_classify_by_extension_and_place(string path, AssetClass expected)
    {
        await Assert.That(AssetClassifier.Classify(s_assets, path)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(AssetClass.Sidecar, false)]
    [Arguments(AssetClass.Junk, false)]
    [Arguments(AssetClass.Foreign, true)]
    [Arguments(AssetClass.Prefab, true)]
    [Arguments(AssetClass.Config, true)]
    [Arguments(AssetClass.Manifest, true)]
    public async Task only_sidecars_and_junk_go_without_a_sidecar(AssetClass assetClass, bool expected)
    {
        await Assert.That(AssetClassifier.NeedsSidecar(assetClass)).IsEqualTo(expected);
    }

    [Test]
    public async Task a_scene_sidecar_would_be_a_sidecar_not_a_scene()
    {
        // Every asset has a sidecar, prefabs included, so this path is normal. Pinned because
        // the suffix check must win over the prefab check: classified as a prefab, the sidecar
        // would be parsed as a document and refused, and verify could never see it as an orphan.
        await Assert.That(AssetClassifier.Classify(s_assets, "/game/assets/levels/a.prefab.meta"))
            .IsEqualTo(AssetClass.Sidecar);
    }

    /// <summary>
    /// The chain is walked backwards, so <see cref="AssetImporters.All"/> reads lowest
    /// precedence first. Pinned because the ORDER is the extension mechanism: an importer
    /// appended by a project has to end up ahead of the built-ins, not behind them.
    /// </summary>
    [Test]
    public async Task the_chain_is_declared_lowest_precedence_first()
    {
        await Assert.That(AssetImporters.All[0]).IsTypeOf<ConfigImporter>();
        await Assert.That(AssetImporters.All[^1]).IsTypeOf<TextureImporter>();
    }
}
