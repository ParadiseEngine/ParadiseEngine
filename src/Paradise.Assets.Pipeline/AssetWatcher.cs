using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// Watches <c>assets/</c> and keeps the tree honest while you work: sidecars first, then a rebuild.
/// </summary>
/// <remarks>
/// <para>
/// All the RULES are <see cref="SidecarMaintainer"/>'s; this owns only what needs a clock — the
/// debounce, the quarantine window, and the loop. That split is what lets every rule be tested
/// without one.
/// </para>
/// <para>
    /// <b>Its own writes must not wake it.</b> The watcher writes sidecars INTO the tree it is
    /// watching, so a mint fires a Created. If that were queued, draining it would write again
    /// itself forever. Created, Changed, and Renamed on a sidecar are therefore ignored
    /// (<see cref="Ignored"/>). A sidecar <em>delete</em> is the other case: the author spent the
    /// identity, and if the asset is still there the maintainer must mint a replacement rather than
    /// leave verify to fail the next rebuild. That delete is rewritten as an Ensure of the asset,
    /// never as a quarantine of the <c>.meta</c> path.
/// </para>
/// <para>
/// <b>An atomic write is not one event.</b> Both Blender and this CLI write temp-then-rename, and
/// editors save in bursts, so a single logical save arrives as several events on several paths.
/// Everything coalesces into a quiet window before anything acts.
/// </para>
/// <para>
/// The rebuild is a plain <see cref="BuildRunner"/> run, not a per-asset path. The runner is
/// incremental now, so "rebuild what changed" IS "run the build" — and inventing a second notion of
/// what is stale would be a second thing to keep in agreement with the first.
/// </para>
/// </remarks>
public sealed class AssetWatcher : IDisposable
{
    /// <summary>How long a path must be quiet before it is acted on.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    /// <summary>How long a deleted asset's identity is held for a matching add.</summary>
    /// <remarks>
    /// Generous, because the cost of it being too short is a move that mints a new GUID and orphans
    /// every reference, while the cost of it being too long is a little memory. A delete that was
    /// really a delete leaves its sidecar on disk either way -- expiry only forgets the chance to
    /// re-link, it never removes anything.
    /// </remarks>
    public static readonly TimeSpan QuarantineWindow = TimeSpan.FromSeconds(30);

    private readonly IFileSystem _fileSystem;
    private readonly AssetProjectLayout _layout;
    private readonly SidecarMaintainer _maintainer;
    private readonly Action<string> _log;
    private readonly Func<DateTimeOffset> _now;

    // `object` and not `System.Threading.Lock`, deliberately: Coyote (1.7.11) rewrites
    // Monitor.Enter/Exit and does not intercept Lock.EnterScope, so with the newer type it
    // cannot CONTROL this lock -- every iteration of Paradise.Assets.Pipeline.CoyoteTest reported
    // the wait as a potential hang, and suppressing that would only have hidden the fact that the
    // interleavings around this gate were never being explored. The lock is held for a few
    // dictionary operations a few times a second, so the newer type buys nothing measurable here
    // and costs the only systematic test this class has. Do not "modernize" it back.
    private readonly object _gate = new();
    private readonly Dictionary<UPath, DateTimeOffset> _pending = [];
    private readonly Dictionary<UPath, (UPath From, DateTimeOffset At)> _renames = [];
    private readonly Dictionary<UPath, DateTimeOffset> _deleted = [];

    private IFileSystemWatcher? _watcher;

    /// <summary>Creates a watcher over one project.</summary>
    public AssetWatcher(
        IFileSystem fileSystem,
        AssetProjectLayout layout,
        SidecarMaintainer maintainer,
        Action<string>? log = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(maintainer);

        _fileSystem = fileSystem;
        _layout = layout;
        _maintainer = maintainer;
        _log = log ?? (static _ => { });
        _now = now ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>Whether anything is waiting out its debounce.</summary>
    public bool HasPending
    {
        get { lock (_gate) { return _pending.Count > 0 || _deleted.Count > 0 || _renames.Count > 0; } }
    }

    /// <summary>Starts raising events. Call <see cref="Drain"/> to act on them.</summary>
    public void Start()
    {
        _watcher = _fileSystem.Watch(_layout.Assets);
        _watcher.IncludeSubdirectories = true;
        _watcher.Created += (_, e) => Observe(e.FullPath);
        _watcher.Changed += (_, e) => Observe(e.FullPath);
        _watcher.Deleted += (_, e) => ObserveDelete(e.FullPath);
        _watcher.Renamed += (_, e) => ObserveRename(e.OldFullPath, e.FullPath);
        _watcher.Error += (_, e) => _log($"watch: the filesystem watcher faulted — {e.Exception.Message}");
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Records an add or a change. Public so tests can drive it without a disk.</summary>
    public void Observe(UPath path)
    {
        if (Ignored(path)) return;
        lock (_gate) { _pending[path] = _now(); }
    }

    /// <summary>Records a delete.</summary>
    public void ObserveDelete(UPath path)
    {
        // A sidecar going away is not a loop event: the maintainer does not delete the sidecar of
        // an asset that is still there. If the asset remains, Ensure mints a new identity; if it
        // went too, Ensure is a no-op (the asset delete is the one that quarantines).
        if (SidecarMeta.IsSidecarPath(path))
        {
            Observe(SidecarMeta.AssetPathFor(path));
            return;
        }

        lock (_gate) { _deleted[path] = _now(); }
    }

    /// <summary>Records a rename, which is the only move that announces itself as one.</summary>
    public void ObserveRename(UPath from, UPath to)
    {
        if (Ignored(to)) return;
        lock (_gate) { _renames[to] = (from, _now()); }
    }

    /// <summary>
    /// Acts on everything that has been quiet for <see cref="Debounce"/>. Returns how many
    /// sidecars it touched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DELETES ARE HANDLED FIRST, and the order is the whole point: a move seen as delete-then-add
    /// only re-links if the identity is already in quarantine when the add is considered. Doing
    /// adds first would mint a new GUID and then quarantine the old one, which is the failure this
    /// is built to avoid, arrived at by scheduling.
    /// </para>
    /// <para>
    /// <b>ONE DRAINER.</b> <see cref="_gate"/> guards the event maps, and the observe methods are
    /// free-threaded against it — but the maintainer is driven OUTSIDE the lock, deliberately (its
    /// work is filesystem IO, and holding a lock across it would stall every incoming event), and
    /// <see cref="SidecarMaintainer"/> keeps its quarantine unsynchronized. So calls to this method
    /// must not overlap: the CLI's loop and the Coyote suite each drive a single drainer. Two
    /// concurrent drains would race the quarantine and lose the identity a move depends on.
    /// </para>
    /// </remarks>
    public int Drain()
    {
        var now = _now();
        List<UPath> deletes;
        List<(UPath To, UPath From)> renames;
        List<UPath> touched;

        lock (_gate)
        {
            deletes = Ripe(_deleted, now);
            renames = [.. Ripe(_renames.ToDictionary(e => e.Key, e => e.Value.At), now)
                .Select(to => (to, _renames[to].From))];
            foreach (var (to, _) in renames) _renames.Remove(to);
            touched = Ripe(_pending, now);
        }

        var actions = 0;
        foreach (var path in deletes)
        {
            if (_maintainer.Quarantine(path, now) != SidecarAction.None) actions++;
        }

        foreach (var (to, from) in renames)
        {
            if (_maintainer.Carry(from, to) != SidecarAction.None) actions++;
        }

        foreach (var path in touched)
        {
            if (_maintainer.Ensure(path) != SidecarAction.None) actions++;
        }

        // Only what a move could plausibly still be. Expiry forgets the chance to re-link; it
        // never removes a sidecar, so a real delete just leaves an orphan for `verify` to report.
        _maintainer.Expire(held => now - held.At > QuarantineWindow);
        return actions;
    }

    /// <summary>
    /// Reconciles sidecars, then runs one build, so the mounted tree matches what was just fixed.
    /// </summary>
    /// <remarks>
    /// Reconcile first, because Rebuild-now does not wait out the debounce of a sidecar delete,
    /// and because a wipe of every <c>.meta</c> arrives as events the loop would otherwise ignore
    /// until the next asset save. The one-shot <c>build</c> verb still refuses a missing sidecar:
    /// watch is the tooling that mints.
    /// </remarks>
    public BuildResult Rebuild(string? profile, ProjectOutputTarget target, ITextureEncoder? encoder)
    {
        _maintainer.Reconcile();
        return new BuildRunner(_fileSystem, _layout, encoder, _log, message => _log($"warning: {message}"))
            .Run(profile, target);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    /// <summary>Whether a Created/Changed/Renamed event is one to act on at all.</summary>
    /// <remarks>
    /// Sidecar writes are the maintainer's own (a mint) or the author editing an
    /// identity by hand. Either way, acting on them would loop. Deletes of sidecars are handled
    /// in <see cref="ObserveDelete"/> instead of here, because those are not writes.
    /// </remarks>
    private static bool Ignored(UPath path) => SidecarMeta.IsSidecarPath(path);

    /// <summary>Takes everything quiet long enough, removing it from the queue.</summary>
    private static List<UPath> Ripe(Dictionary<UPath, DateTimeOffset> queue, DateTimeOffset now)
    {
        var ripe = queue.Where(entry => now - entry.Value >= Debounce).Select(entry => entry.Key).ToList();
        foreach (var path in ripe) queue.Remove(path);
        return ripe;
    }
}
