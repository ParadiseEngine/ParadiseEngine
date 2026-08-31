using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// The watcher's own rules, driven without a disk: <see cref="AssetWatcher.Observe"/> and friends
/// are public for exactly this, and the clock is injected, so the debounce is data rather than a
/// sleep.
/// </summary>
public class AssetWatcherTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");
    private static readonly DateTimeOffset s_start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A watcher whose clock the test moves by assigning to the returned box.</summary>
    private static (AssetWatcher Watcher, MemoryFileSystem FileSystem, Clock Clock) Watching()
    {
        var fileSystem = ProjectVerifierTests.CreateProject();
        var clock = new Clock { Now = s_start };
        var maintainer = new SidecarMaintainer(fileSystem, s_layout);
        return (new AssetWatcher(fileSystem, s_layout, maintainer, now: () => clock.Now), fileSystem, clock);
    }

    private sealed class Clock
    {
        public DateTimeOffset Now;
    }

    private static void WriteAsset(MemoryFileSystem fileSystem, UPath path, byte[] bytes)
    {
        fileSystem.CreateDirectory(path.GetDirectory());
        fileSystem.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// The loop guard, and the only thing standing between the watcher and itself.
    /// </summary>
    /// <remarks>
    /// The maintainer writes sidecars INTO the watched tree, so a mint fires a Created and a hash
    /// refresh fires a Changed. If either were queued, draining it would write again and the
    /// watcher would run forever on one edit. This is pinned across all three event shapes
    /// because a per-path suppression window used to back it up and no longer does — the rule is
    /// now carried by one line.
    /// </remarks>
    [Test]
    public async Task no_sidecar_event_is_ever_queued()
    {
        var (watcher, _, _) = Watching();
        using var _guard = watcher;

        watcher.Observe("/game/assets/models/crate.glb.meta");
        watcher.ObserveDelete("/game/assets/models/gone.glb.meta");
        watcher.ObserveRename("/game/assets/models/a.glb.meta", "/game/assets/models/b.glb.meta");

        await Assert.That(watcher.HasPending).IsFalse();
    }

    /// <summary>
    /// The same rule stated as the behaviour it exists for: draining the maintainer's own write
    /// leaves nothing to drain again.
    /// </summary>
    [Test]
    public async Task the_maintainers_own_write_does_not_come_back_as_work()
    {
        var (watcher, fileSystem, clock) = Watching();
        using var _guard = watcher;
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);

        watcher.Observe("/game/assets/models/crate.glb");
        clock.Now += AssetWatcher.Debounce;
        var minted = watcher.Drain();

        // The mint happened, and the sidecar it produced is on disk -- so the Created event a
        // real filesystem would now raise is the one the guard has to swallow.
        await Assert.That(minted).IsEqualTo(1);
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb.meta")).IsTrue();

        watcher.Observe("/game/assets/models/crate.glb.meta");
        clock.Now += AssetWatcher.Debounce;

        await Assert.That(watcher.HasPending).IsFalse();
        await Assert.That(watcher.Drain()).IsEqualTo(0);
    }

    [Test]
    public async Task an_edit_waits_out_its_debounce()
    {
        var (watcher, fileSystem, clock) = Watching();
        using var _guard = watcher;
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);

        watcher.Observe("/game/assets/models/crate.glb");

        // Still warm: an atomic write arrives as several events, so acting on the first one would
        // act on a half-written file.
        await Assert.That(watcher.Drain()).IsEqualTo(0);
        await Assert.That(watcher.HasPending).IsTrue();

        clock.Now += AssetWatcher.Debounce;

        await Assert.That(watcher.Drain()).IsEqualTo(1);
        await Assert.That(watcher.HasPending).IsFalse();
    }

    /// <summary>
    /// A move seen as delete-then-add keeps its identity, which is what the delete-before-add
    /// ordering inside one drain exists to produce.
    /// </summary>
    [Test]
    public async Task a_move_drained_in_one_pass_keeps_the_assets_identity()
    {
        var (watcher, fileSystem, clock) = Watching();
        using var _guard = watcher;

        // Minted through the watcher, not by hand: only Ensure records the content hash, and the
        // hash is the sole thing that can recognise the asset at its new path later.
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);
        watcher.Observe("/game/assets/models/crate.glb");
        clock.Now += AssetWatcher.Debounce;
        watcher.Drain();
        var original = SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta");

        // The move, as a filesystem reports it when it does not recognise one: the asset is gone
        // from the old path and present at the new one. The old sidecar stays behind — that is
        // what a plain `mv` of the asset leaves, and it is where the identity is read from.
        fileSystem.DeleteFile("/game/assets/models/crate.glb");
        WriteAsset(fileSystem, "/game/assets/props/crate.glb", [1, 2, 3]);

        watcher.Observe("/game/assets/props/crate.glb");
        watcher.ObserveDelete("/game/assets/models/crate.glb");
        clock.Now += AssetWatcher.Debounce;
        watcher.Drain();

        var carried = SidecarMeta.Load(fileSystem, "/game/assets/props/crate.glb.meta");
        await Assert.That(carried.Guid).IsEqualTo(original.Guid);
    }
}
