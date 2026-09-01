using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.CoyoteTest;

/// <summary>
/// <see cref="AssetWatcher"/>'s queue under systematic exploration.
///
/// The watcher has two sides on two threads. The filesystem watcher's callbacks land on ITS
/// thread and record events (<see cref="AssetWatcher.Observe"/> and friends); the watch loop
/// drains them on ANOTHER. Four dictionaries carry that state behind one lock, and
/// <see cref="AssetWatcher.Drain"/> reads all of them as a SET — deletes taken before adds,
/// because a move seen as delete-then-add only re-links if the identity is already quarantined
/// when the add is considered.
///
/// THE INVARIANT, in one sentence: an observed edit is either still queued or has been acted on,
/// never neither. An event dropped between the two is a file the author changed and the build
/// never rebuilds — silent staleness, discovered much later as "the game is running old data".
///
/// Why systematically rather than by a stress loop: the window is the few instructions between
/// <c>Ripe</c>'s enumeration and its removals, and the repo has already been bitten once by a
/// hand-written race test passing three runs out of three against genuinely broken code. These
/// also stand as the guard on a specific temptation — swapping the dictionaries for
/// <c>ConcurrentDictionary</c>. That would make each operation atomic and the SET of them not,
/// and <see cref="ObservesRacingOneDrain_LoseNoEdit"/> is what would notice.
///
/// Only the draining task touches the filesystem here. The producers call nothing but the
/// watcher, so what is being explored is the watcher's own lock rather than Zio's.
/// </summary>
public static class AssetWatcherTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");
    private static readonly DateTimeOffset s_start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A clock that moves one <see cref="AssetWatcher.Debounce"/> per reading.
    /// </summary>
    /// <remarks>
    /// Time is the thing under test, so it is not left to the wall clock — a real one would make
    /// "was this ripe yet" depend on how long Coyote took to schedule, and the same interleaving
    /// would pass or fail run to run. Stepping by a full debounce means anything recorded before
    /// a drain reads the clock is ripe to that drain, so a lost event cannot hide behind "not
    /// due yet".
    /// </remarks>
    private sealed class SteppingClock
    {
        private long _steps;

        public DateTimeOffset Now() => s_start + (AssetWatcher.Debounce * Interlocked.Increment(ref _steps));
    }

    private static (AssetWatcher Watcher, MemoryFileSystem FileSystem) Watching()
    {
        var fileSystem = new MemoryFileSystem();
        fileSystem.CreateDirectory("/game/assets");
        fileSystem.WriteAllText("/game/assets/project.toml", "name = \"probe\"\nschema_version = 1\n");
        SidecarMeta.Mint().Save(fileSystem, "/game/assets/project.toml.meta");

        var clock = new SteppingClock();
        var maintainer = new SidecarMaintainer(fileSystem, s_layout);
        return (new AssetWatcher(fileSystem, s_layout, maintainer, now: clock.Now), fileSystem);
    }

    private static UPath Asset(MemoryFileSystem fileSystem, string name)
    {
        UPath path = "/game/assets/" + name;
        fileSystem.CreateDirectory(path.GetDirectory());
        fileSystem.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    /// <summary>
    /// Two watcher callbacks racing one drain: every edit is acted on.
    ///
    /// The shape of the real thing. A save touches several files, the filesystem reports them on
    /// its own thread, and the watch loop is already draining when they arrive. What must not
    /// happen is an edit that lands in the queue and then evaporates — taken out of the
    /// dictionary by a drain that does not go on to act on it, or overwritten by one that does.
    /// </summary>
    [Test]
    public static async Task ObservesRacingOneDrain_LoseNoEdit()
    {
        var (watcher, fileSystem) = Watching();
        using var guard = watcher;
        var first = Asset(fileSystem, "models/crate.glb");
        var second = Asset(fileSystem, "models/barrel.glb");

        // Distinct paths on purpose: the queue COALESCES repeats of one path by design, so two
        // observations of the same file legitimately produce one action and would prove nothing.
        var producers = new[]
        {
            Task.Run(() => watcher.Observe(first)),
            Task.Run(() => watcher.Observe(second)),
        };
        var consumer = Task.Run(() => watcher.Drain());

        await Task.WhenAll([.. producers, consumer]).ConfigureAwait(false);

        // The settling drain: whatever arrived too late for the racing one is still queued, which
        // is correct and not a loss. Draining once more is what the watch loop does anyway.
        watcher.Drain();

        // A sidecar on disk is the proof the event was acted on -- Ensure mints one, and nothing
        // else in this test writes them.
        Specification.Assert(
            fileSystem.FileExists(SidecarMeta.PathFor(first)),
            "An observed edit was neither queued nor acted on: that file never rebuilds.");
        Specification.Assert(
            fileSystem.FileExists(SidecarMeta.PathFor(second)),
            "An observed edit was neither queued nor acted on: that file never rebuilds.");
        Specification.Assert(
            !watcher.HasPending,
            "The queue still holds work after a settling drain.");
    }

    /// <summary>
    /// Renames arriving while a drain is taking them: the four-step read holds.
    ///
    /// <see cref="AssetWatcher.Drain"/> touches <c>_renames</c> four times in a row — snapshot it,
    /// ripen the copy, take each entry's source path, then remove it — on the assumption that
    /// nothing moved underneath. That assumption is the most fragile thing in the class, and it
    /// is the first casualty of "just use a ConcurrentDictionary": per-operation atomicity would
    /// leave every one of those four steps individually safe and the sequence wrong.
    ///
    /// TWO renamers, not one. A single writer racing the drain's READS is not a write/write
    /// conflict, and an earlier version of this test with one renamer passed against a Drain
    /// whose lock had been deleted — a guard nobody had checked. Two writers on the same
    /// dictionary is what makes the missing lock visible.
    /// </summary>
    [Test]
    public static async Task RenamesRacingOneDrain_KeepTheQueueIntact()
    {
        var (watcher, fileSystem) = Watching();
        using var guard = watcher;
        var removed = Asset(fileSystem, "models/gone.glb");
        SidecarMeta.Mint().Save(fileSystem, SidecarMeta.PathFor(removed));
        var first = Asset(fileSystem, "models/old-a.glb");
        SidecarMeta.Mint().Save(fileSystem, SidecarMeta.PathFor(first));
        var second = Asset(fileSystem, "models/old-b.glb");
        SidecarMeta.Mint().Save(fileSystem, SidecarMeta.PathFor(second));

        var producers = new[]
        {
            Task.Run(() => watcher.ObserveRename(first, "/game/assets/models/new-a.glb")),
            Task.Run(() => watcher.ObserveRename(second, "/game/assets/models/new-b.glb")),
            // A delete beside them, so the drain is reading all three dictionaries as a set
            // rather than just the one under contention.
            Task.Run(() => watcher.ObserveDelete(removed)),
        };
        var consumer = Task.Run(() => watcher.Drain());

        await Task.WhenAll([.. producers, consumer]).ConfigureAwait(false);

        watcher.Drain();

        Specification.Assert(
            !watcher.HasPending,
            "Work was left in the queue after a settling drain: an event the loop will never act on.");

        // Carry moves the identity to the new path and removes the old sidecar. A rename taken
        // from a corrupted snapshot would carry the wrong source, or none.
        Specification.Assert(
            fileSystem.FileExists("/game/assets/models/new-a.glb.meta")
                && fileSystem.FileExists("/game/assets/models/new-b.glb.meta"),
            "A rename was drained without its identity reaching the new path.");
    }

    /// <summary>
    /// Recording alone, with nobody draining: concurrent callbacks do not lose each other.
    ///
    /// Narrower than the tests above and cheaper to explore, so it isolates the writer side. If
    /// the dictionaries were ever made lock-free this is the first thing that would go, and it
    /// would go without an exception — two writers, one surviving entry.
    /// </summary>
    [Test]
    public static async Task ConcurrentObserves_AreAllRecorded()
    {
        var (watcher, fileSystem) = Watching();
        using var guard = watcher;
        var first = Asset(fileSystem, "models/a.glb");
        var second = Asset(fileSystem, "models/b.glb");
        var third = Asset(fileSystem, "models/c.glb");

        await Task.WhenAll(
            Task.Run(() => watcher.Observe(first)),
            Task.Run(() => watcher.Observe(second)),
            Task.Run(() => watcher.Observe(third))).ConfigureAwait(false);

        watcher.Drain();

        foreach (var path in new[] { first, second, third })
        {
            Specification.Assert(
                fileSystem.FileExists(SidecarMeta.PathFor(path)),
                "A concurrently recorded edit never reached the drain.");
        }
    }
}
