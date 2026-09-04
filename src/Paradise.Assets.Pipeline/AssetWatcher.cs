using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// Watches <c>assets/</c> and keeps the tree honest while you work: sidecars first, then a rebuild.
/// </summary>
/// <remarks>
/// The rules are <see cref="SidecarMaintainer"/>'s; this owns only what needs a clock, so every
/// rule is testable without one. Sidecar Created/Changed/Renamed events are ignored because the
/// watcher's own mints would otherwise wake it forever; a sidecar delete instead becomes an
/// Ensure of the asset, so a spent identity is re-minted rather than left for verify. One logical
/// save arrives as several events (temp-then-rename, editor bursts), hence the quiet window; a
/// temp file that outlives it can carry a fresh identity over the real one (issue #196). The
/// rebuild is a plain incremental <see cref="BuildRunner"/> run so there is no second notion of
/// "stale" to keep in agreement with the index.
/// </remarks>
public sealed partial class AssetWatcher : IDisposable
{
    /// <summary>How long a path must be quiet before it is acted on.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    /// <summary>How long a deleted asset's identity is held for a matching add; generous, because too short orphans every reference and too long costs a little memory.</summary>
    public static readonly TimeSpan QuarantineWindow = TimeSpan.FromSeconds(30);

    private readonly IFileSystem _fileSystem;
    private readonly AssetProjectLayout _layout;
    private readonly SidecarMaintainer _maintainer;
    private readonly ILogger _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly IReadOnlyList<IAssetImporter> _importers;

    // `object`, not `System.Threading.Lock`: Coyote (1.7.11) rewrites Monitor.Enter/Exit but not
    // Lock.EnterScope, so with the newer type Paradise.Assets.Pipeline.CoyoteTest cannot control
    // this lock and reports every wait as a hang. Do not "modernize" it back.
    private readonly object _gate = new();
    private readonly Dictionary<UPath, DateTimeOffset> _pending = [];
    private readonly Dictionary<UPath, (UPath From, DateTimeOffset At)> _renames = [];
    private readonly Dictionary<UPath, DateTimeOffset> _deleted = [];

    private IFileSystemWatcher? _watcher;

    /// <summary>Creates a watcher over one project; <paramref name="importers"/> is the chain every rebuild runs (the built-ins when omitted).</summary>
    public AssetWatcher(
        IFileSystem fileSystem,
        AssetProjectLayout layout,
        SidecarMaintainer maintainer,
        ILogger? logger = null,
        Func<DateTimeOffset>? now = null,
        IReadOnlyList<IAssetImporter>? importers = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(maintainer);

        _fileSystem = fileSystem;
        _layout = layout;
        _maintainer = maintainer;
        _log = logger ?? NullLogger.Instance;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _importers = importers ?? AssetImporters.All;
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
        _watcher.Error += (_, e) => LogWatcherFaulted(_log, e.Exception.Message);
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
        // Not a loop event: the maintainer never deletes the sidecar of an asset still present.
        if (SidecarMeta.IsSidecarPath(path))
        {
            Observe(SidecarMeta.AssetPathFor(path));
            return;
        }

        if (Ignored(path)) return;

        lock (_gate) { _deleted[path] = _now(); }
    }

    /// <summary>Records a rename, which is the only move that announces itself as one.</summary>
    public void ObserveRename(UPath from, UPath to)
    {
        if (Ignored(to)) return;
        lock (_gate) { _renames[to] = (from, _now()); }
    }

    /// <summary>Acts on everything quiet for <see cref="Debounce"/>.</summary>
    /// <remarks>
    /// Deletes before adds, or a move seen as delete-then-add would mint a new GUID before the old
    /// one reached quarantine. The maintainer runs outside the lock (its work is IO) and keeps its
    /// quarantine unsynchronized, so drains must not overlap: one drainer per process.
    /// </remarks>
    public DrainResult Drain()
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

        _maintainer.Expire(held => now - held.At > QuarantineWindow);
        return new DrainResult(deletes.Count + renames.Count + touched.Count, actions);
    }

    /// <summary>Reconciles sidecars, then builds; reconcile first because rebuild-now does not wait out the debounce, and a wipe of every <c>.meta</c> would otherwise sit unnoticed until the next asset save.</summary>
    public BuildResult Rebuild(string? profile, ProjectOutputTarget target, ITextureEncoder? encoder)
    {
        _maintainer.Reconcile();
        StampMeshes();
        // One logger through, where this used to synthesise a second delegate that prefixed
        // "warning: " — the severity is BuildRunner's to state as a level now.
        return new BuildRunner(_fileSystem, _layout, encoder, _log, _importers)
            .Run(profile, target);
    }

    /// <summary>Puts <c>extras.paradise</c> onto every GLB image that lacks it — a reconcile of references the way <see cref="SidecarMaintainer.Reconcile"/> is one of identities. Stamps only; uris are followed on a rename (<see cref="Drain"/>), never under an author's feet at build time.</summary>
    public int StampMeshes()
    {
        var index = AssetIndex.Scan(_fileSystem, _layout.Assets, _maintainer.Ignore);
        var stamped = ReferenceRepair.StampMeshes(_fileSystem, _layout, index);
        foreach (var mesh in stamped) LogStamped(_log, index.Relative(mesh.Path), mesh.Repointed.Count);
        return stamped.Count;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    private bool Ignored(UPath path) => SidecarMeta.IsSidecarPath(path) || _maintainer.Ignore.Matches(_layout.Assets, path);

    private static List<UPath> Ripe(Dictionary<UPath, DateTimeOffset> queue, DateTimeOffset now)
    {
        var ripe = queue.Where(entry => now - entry.Value >= Debounce).Select(entry => entry.Key).ToList();
        foreach (var path in ripe) queue.Remove(path);
        return ripe;
    }

    // Warning, not Information: the watcher faulting means edits stop being noticed, which the
    // watch loop cannot tell the author any other way.
    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "watch: the filesystem watcher faulted — {Reason}")]
    private static partial void LogWatcherFaulted(ILogger logger, string reason);

    [LoggerMessage(EventId = 13, Level = LogLevel.Information, Message = "stamped: {Relative} ({Images} image reference(s) by identity)")]
    private static partial void LogStamped(ILogger logger, string relative, int images);
}

/// <summary>What one <see cref="AssetWatcher.Drain"/> did.</summary>
/// <param name="Changes">Ripe events acted on: edits, adds, deletes and renames. Any of them
/// changes what a build would produce, so this is what the watch loop rebuilds on. Sidecar work
/// alone is not it: an asset that already has one reports zero sidecar actions on every edit, and
/// gating on that left content edits unbuilt (issue #195).</param>
/// <param name="SidecarActions">Sidecars minted, carried, quarantined, relinked or refreshed.</param>
public readonly record struct DrainResult(int Changes, int SidecarActions);
