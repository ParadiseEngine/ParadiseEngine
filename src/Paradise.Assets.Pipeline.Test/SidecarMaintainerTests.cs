using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

public class SidecarMaintainerTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");
    private static readonly DateTimeOffset s_now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SidecarMaintainer Maintainer(MemoryFileSystem fileSystem, bool dryRun = false)
        => new(fileSystem, s_layout, dryRun: dryRun);

    private static void WriteAsset(MemoryFileSystem fileSystem, UPath path, byte[] bytes)
    {
        fileSystem.CreateDirectory(path.GetDirectory());
        fileSystem.WriteAllBytes(path, bytes);
    }

    [Test]
    public async Task an_asset_without_a_sidecar_gets_one()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);

        var action = Maintainer(fileSystem).Ensure("/game/assets/models/crate.glb");

        await Assert.That(action).IsEqualTo(SidecarAction.Minted);
        var meta = SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta");
        await Assert.That(meta.Hash).IsNull();
        await Assert.That(meta.Write()).DoesNotContain("hash");
    }

    [Test]
    public async Task a_changed_asset_keeps_its_identity_and_the_sidecar_is_left_alone()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        WriteAsset(fileSystem, "/game/assets/textures/fire.png", [1, 2, 3]);
        Maintainer(fileSystem).Ensure("/game/assets/textures/fire.png");
        var before = SidecarMeta.Load(fileSystem, "/game/assets/textures/fire.png.meta");

        fileSystem.WriteAllBytes("/game/assets/textures/fire.png", [4, 5, 6]);
        var action = Maintainer(fileSystem).Ensure("/game/assets/textures/fire.png");

        await Assert.That(action).IsEqualTo(SidecarAction.None);
        var after = SidecarMeta.Load(fileSystem, "/game/assets/textures/fire.png.meta");
        await Assert.That(after.Guid).IsEqualTo(before.Guid);
        await Assert.That(after.Hash).IsNull();
    }

    [Test]
    public async Task a_leftover_recorded_hash_is_dropped()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);
        new SidecarMeta(Guid.NewGuid()) { Hash = new string('a', 64) }
            .Save(fileSystem, "/game/assets/models/crate.glb.meta");
        // Save no longer emits hash, so write the old shape by hand.
        fileSystem.WriteAllText(
            "/game/assets/models/crate.glb.meta",
            """
            schema_version = 1
            guid = "3e1c4f60-2f5d-4e7c-a081-9c0d1e2f3041"
            hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

            """);

        var action = Maintainer(fileSystem).Ensure("/game/assets/models/crate.glb");

        await Assert.That(action).IsEqualTo(SidecarAction.Refreshed);
        var meta = SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta");
        await Assert.That(meta.Hash).IsNull();
        await Assert.That(fileSystem.ReadAllText("/game/assets/models/crate.glb.meta")).DoesNotContain("hash");
    }

    [Test]
    public async Task an_up_to_date_sidecar_is_left_alone()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);
        var maintainer = Maintainer(fileSystem);
        maintainer.Ensure("/game/assets/models/crate.glb");

        await Assert.That(maintainer.Ensure("/game/assets/models/crate.glb")).IsEqualTo(SidecarAction.None);
    }

    [Test]
    public async Task a_rename_carries_the_sidecar_unchanged()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);
        var maintainer = Maintainer(fileSystem);
        maintainer.Ensure("/game/assets/models/crate.glb");
        var before = SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta");

        fileSystem.MoveFile("/game/assets/models/crate.glb", "/game/assets/models/box.glb");
        var action = maintainer.Carry("/game/assets/models/crate.glb", "/game/assets/models/box.glb");

        await Assert.That(action).IsEqualTo(SidecarAction.Carried);
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb.meta")).IsFalse();
        await Assert.That(SidecarMeta.Load(fileSystem, "/game/assets/models/box.glb.meta").Guid).IsEqualTo(before.Guid);
    }

    // ---- the one that matters -----------------------------------------------------------

    /// <summary>
    /// A temp-then-rename save whose temp file outlives the debounce: the temp gets a mint, then
    /// the rename would carry that fresh guid over the asset's real one and break every reference
    /// to it (issue #196). The destination wins; the stray sidecar goes.
    /// </summary>
    [Test]
    public async Task a_rename_onto_an_existing_sidecar_keeps_the_destination_identity()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var maintainer = Maintainer(fileSystem);
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);
        maintainer.Ensure("/game/assets/models/crate.glb");
        var real = SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta").Guid;

        WriteAsset(fileSystem, "/game/assets/models/crate.glb.tmp", [4, 5, 6]);
        maintainer.Ensure("/game/assets/models/crate.glb.tmp");
        fileSystem.DeleteFile("/game/assets/models/crate.glb.tmp");
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [4, 5, 6]);

        var action = maintainer.Carry("/game/assets/models/crate.glb.tmp", "/game/assets/models/crate.glb");

        await Assert.That(action).IsEqualTo(SidecarAction.Conflicted);
        await Assert.That(SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta").Guid).IsEqualTo(real);
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb.tmp.meta")).IsFalse();
    }

    [Test]
    public async Task a_delete_then_add_elsewhere_keeps_the_same_guid()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);
        var maintainer = Maintainer(fileSystem);
        maintainer.Ensure("/game/assets/models/crate.glb");
        var before = SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta");

        // What `git mv` looks like on Windows: no rename event, just the two halves.
        fileSystem.DeleteFile("/game/assets/models/crate.glb");
        await Assert.That(maintainer.Quarantine("/game/assets/models/crate.glb", s_now))
            .IsEqualTo(SidecarAction.Quarantined);

        WriteAsset(fileSystem, "/game/assets/props/crate.glb", [1, 2, 3]);
        var action = maintainer.Ensure("/game/assets/props/crate.glb");

        // A new GUID here would orphan every reference to this asset, silently.
        await Assert.That(action).IsEqualTo(SidecarAction.Relinked);
        await Assert.That(SidecarMeta.Load(fileSystem, "/game/assets/props/crate.glb.meta").Guid)
            .IsEqualTo(before.Guid);
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb.meta")).IsFalse();
    }

    [Test]
    public async Task a_quarantine_never_removes_the_sidecar()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);
        var maintainer = Maintainer(fileSystem);
        maintainer.Ensure("/game/assets/models/crate.glb");

        fileSystem.DeleteFile("/game/assets/models/crate.glb");
        maintainer.Quarantine("/game/assets/models/crate.glb", s_now);
        maintainer.Expire(_ => true);

        // A genuine delete leaves an orphan for `verify` to report. Nothing here destroys a GUID.
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb.meta")).IsTrue();
    }

    [Test]
    public async Task an_unrelated_add_after_a_delete_gets_its_own_identity()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);
        var maintainer = Maintainer(fileSystem);
        maintainer.Ensure("/game/assets/models/crate.glb");
        var before = SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta");

        fileSystem.DeleteFile("/game/assets/models/crate.glb");
        maintainer.Quarantine("/game/assets/models/crate.glb", s_now);

        // Different content, so it is a different asset however similar the name.
        WriteAsset(fileSystem, "/game/assets/models/barrel.glb", [7, 7, 7]);
        var action = maintainer.Ensure("/game/assets/models/barrel.glb");

        await Assert.That(action).IsEqualTo(SidecarAction.Minted);
        await Assert.That(SidecarMeta.Load(fileSystem, "/game/assets/models/barrel.glb.meta").Guid)
            .IsNotEqualTo(before.Guid);
    }

    [Test]
    public async Task a_malformed_sidecar_is_left_for_its_author()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);
        fileSystem.WriteAllText("/game/assets/models/crate.glb.meta", "this is not toml = = =");

        var action = Maintainer(fileSystem).Ensure("/game/assets/models/crate.glb");

        // It may hold the only copy of an identity; overwriting it to silence a warning spends that.
        await Assert.That(action).IsEqualTo(SidecarAction.None);
        await Assert.That(fileSystem.ReadAllText("/game/assets/models/crate.glb.meta"))
            .IsEqualTo("this is not toml = = =");
    }

    [Test]
    public async Task a_dry_run_writes_nothing()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);

        var action = Maintainer(fileSystem, dryRun: true).Ensure("/game/assets/models/crate.glb");

        await Assert.That(action).IsEqualTo(SidecarAction.Minted);
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb.meta")).IsFalse();
    }

    [Test]
    public async Task reconcile_brings_a_whole_drifted_tree_into_line()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/crate.glb");
        WriteAsset(fileSystem, "/game/assets/models/barrel.glb", [9, 9]);   // no sidecar at all
        fileSystem.WriteAllBytes("/game/assets/models/crate.glb", [4, 5, 6]); // sidecar now stale

        var touched = Maintainer(fileSystem).Reconcile();

        // The missing sidecar is minted. A changed asset whose sidecar already has a GUID is
        // left alone — content is not recorded in the sidecar.
        await Assert.That(touched).IsEqualTo(1);
        await Assert.That(fileSystem.FileExists("/game/assets/models/barrel.glb.meta")).IsTrue();
        await Assert.That(SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta").Hash).IsNull();
    }
}
