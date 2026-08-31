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
    public async Task what_the_pipeline_recognises_is_the_union_of_the_importers_extensions()
    {
        await Assert.That(AssetImporters.Recognises("/a/x.glb")).IsTrue();
        await Assert.That(AssetImporters.Recognises("/a/x.jpeg")).IsTrue();
        await Assert.That(AssetImporters.Recognises("/a/x.bnk")).IsTrue();
        await Assert.That(AssetImporters.Recognises("/a/x.txt")).IsFalse();
    }

    /// <summary>
    /// Recognition is target-independent, and a sidecar is the case that proves it: only the
    /// play tree carries one, but <c>verify</c> — which builds nothing — must still call it a
    /// file the pipeline knows rather than warn about it.
    /// </summary>
    [Test]
    public async Task a_sidecars_extension_is_recognised_whatever_tree_is_being_built()
    {
        await Assert.That(AssetImporters.Recognises("/a/x" + SidecarMeta.Suffix)).IsTrue();
    }

    /// <summary>
    /// The chain is walked backwards, so <see cref="AssetImporters.All"/> reads lowest
    /// precedence first. Pinned because the ORDER is the extension mechanism: an importer
    /// appended by a project has to end up ahead of the built-ins, not behind them.
    /// </summary>
    [Test]
    public async Task the_chain_is_declared_lowest_precedence_first()
    {
        await Assert.That(AssetImporters.All[0]).IsTypeOf<SidecarImporter>();
        await Assert.That(AssetImporters.All[^1]).IsTypeOf<TextureImporter>();
    }
}
