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
        await Assert.That(meta.Hash).IsEqualTo(SidecarMeta.ComputeHash([1, 2, 3]));
    }

    [Test]
    public async Task a_changed_asset_keeps_its_identity_and_only_the_hash_moves()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        WriteAsset(fileSystem, "/game/assets/textures/fire.png", [1, 2, 3]);
        Maintainer(fileSystem).Ensure("/game/assets/textures/fire.png");
        var before = SidecarMeta.Load(fileSystem, "/game/assets/textures/fire.png.meta");

        fileSystem.WriteAllBytes("/game/assets/textures/fire.png", [4, 5, 6]);
        var action = Maintainer(fileSystem).Ensure("/game/assets/textures/fire.png");

        await Assert.That(action).IsEqualTo(SidecarAction.Refreshed);
        var after = SidecarMeta.Load(fileSystem, "/game/assets/textures/fire.png.meta");
        await Assert.That(after.Guid).IsEqualTo(before.Guid);
        await Assert.That(after.Hash).IsEqualTo(SidecarMeta.ComputeHash([4, 5, 6]));
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

        // Three: the stale sidecar, the missing one, and the project manifest's — which the
        // fixture mints with no hash at all. Filling that in is the job, not an accident: the
        // field is "optional in the format, always written by tooling", and this is the tooling.
        await Assert.That(touched).IsEqualTo(3);
        await Assert.That(SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta").Hash)
            .IsEqualTo(SidecarMeta.ComputeHash([4, 5, 6]));
        await Assert.That(fileSystem.FileExists("/game/assets/models/barrel.glb.meta")).IsTrue();
    }
}
