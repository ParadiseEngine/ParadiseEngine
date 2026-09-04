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

    // Dependents a rename could not catch up because they were mid-edit; retried every drain.
    private readonly HashSet<UPath> _deferred = [];

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

        var carried = new List<UPath>();
        foreach (var (to, from) in renames)
        {
            var action = _maintainer.Carry(from, to);
            if (action != SidecarAction.None) actions++;
            if (action is SidecarAction.Carried or SidecarAction.Relinked) carried.Add(to);
        }

        foreach (var path in touched)
        {
            var action = _maintainer.Ensure(path);
            if (action != SidecarAction.None) actions++;
            if (action == SidecarAction.Relinked) carried.Add(path);
            actions += MintReferences(path);
        }

        var expired = _maintainer.Expire(held => now - held.At > QuarantineWindow);

        var rewritten = carried.Count > 0 ? FollowRenames(carried) : 0;
        rewritten += RetryDeferred();
        IReadOnlyList<string> dangling = expired.Count > 0 ? ReportDangling(expired) : [];
        return new DrainResult(deletes.Count + renames.Count + touched.Count, actions, rewritten, dangling);
    }

    /// <summary>
    /// After an identity moved, every file that references it has its path half (a document's
    /// reference, a mesh's uri) caught up, so a rename done in Finder leaves the tree as tidy as
    /// <c>mv</c> would. Only the dependents, through the graph — and not one that is itself
    /// mid-edit (still pending its debounce): it is rewritten on the next drain instead.
    /// </summary>
    /// <remarks>
    /// A rewrite is a document write, which comes back as a change: correct (it rebuilds) and
    /// finite (the second pass finds nothing stale). A document open in an editor sees its stamp
    /// move and refuses its next save until reloaded, which is the right outcome — that copy is
    /// behind. Dry-run reports and writes nothing.
    /// </remarks>
    private int FollowRenames(IReadOnlyList<UPath> carried)
    {
        var index = AssetIndex.Scan(_fileSystem, _layout.Assets, _maintainer.Ignore);
        var graph = ReferenceGraph.Build(_fileSystem, _layout, index, _maintainer.Ignore, _importers);

        // The carried assets themselves too: a mesh's uris are relative to it.
        var dependents = new List<UPath>(carried);
        foreach (var path in carried)
        {
            if (index.IdentityOf(path) is { } guid) dependents.AddRange(graph.DependentFilesOf(guid));
        }

        return CatchUp(index, dependents.Distinct());
    }

    /// <summary>
    /// A GLB with geometry gets its mesh, skeleton and clip reference documents on the spot: they
    /// are tool-owned, carry no author work, and a re-export that adds a clip should add its
    /// document without a verb. Materials, textures and the prefab are the author's from the
    /// moment they exist, so those are offered, never written — extraction of them mints files an
    /// author edits, which is not a watcher's to do on a save.
    /// </summary>
    private int MintReferences(UPath path)
    {
        if (!MeshContainer.IsMesh(path) || !_fileSystem.FileExists(path)) return 0;
        var sidecar = SidecarMeta.PathFor(path);
        var bytes = _fileSystem.ReadAllBytes(path);
        if (!_fileSystem.FileExists(sidecar) || !MeshContainer.HasGeometry(path, bytes)) return 0;

        var relative = path.FullName[(_layout.Assets.FullName.Length + 1)..];
        var result = AssetExtractor.MintReferences(_fileSystem, _layout, path, _importers, _log);
        foreach (var error in result.Errors) LogMintRefused(_log, error);
        foreach (var written in result.Written) LogMinted(_log, written.ToString());

        try
        {
            if (AssetExtractor.HasAuthoredParts(bytes) && !GlbImportSettings.ReadExtraction(SidecarMeta.Load(_fileSystem, sidecar)).Authored) LogOffer(_log, relative);
        }
        catch (SidecarMetaException)
        {
            // verify's finding
        }

        return result.Written.Count;
    }

    /// <summary>Whatever an earlier pass deferred and is quiet now.</summary>
    private int RetryDeferred()
    {
        if (_deferred.Count == 0) return 0;
        var index = AssetIndex.Scan(_fileSystem, _layout.Assets, _maintainer.Ignore);
        return CatchUp(index, [.. _deferred]);
    }

    private int CatchUp(AssetIndex index, IEnumerable<UPath> files)
    {
        var rewritten = 0;
        foreach (var path in files)
        {
            lock (_gate)
            {
                if (_pending.ContainsKey(path))
                {
                    if (_deferred.Add(path)) LogDeferred(_log, index.Relative(path));
                    continue;
                }
            }

            _deferred.Remove(path);
            if (_maintainer.DryRun)
            {
                LogWouldRewrite(_log, index.Relative(path));
                continue;
            }

            var context = new ReferenceContext(_fileSystem, _layout, index, _maintainer.Ignore);
            if (ReferenceChain.Rewrite(_importers, context, path) is not { } repaired) continue;
            rewritten++;
            LogRewrote(_log, index.Relative(path), repaired.Repointed.Count);
        }

        return rewritten;
    }

    /// <summary>What a delete that really was one left dangling: the graph still holds every edge into the identity that is gone.</summary>
    private List<string> ReportDangling(IReadOnlyList<QuarantinedIdentity> expired)
    {
        var index = AssetIndex.Scan(_fileSystem, _layout.Assets, _maintainer.Ignore);
        var graph = ReferenceGraph.Build(_fileSystem, _layout, index, _maintainer.Ignore, _importers);

        var dangling = new List<string>();
        foreach (var held in expired)
        {
            foreach (var edge in graph.DependentsOf(held.Meta.Guid))
            {
                var message = $"{index.Relative(held.Asset)} is gone but {index.Relative(edge.ReferrerPath)} in {edge.Where} still names it ('{edge.Path}')";
                dangling.Add(message);
                LogDangling(_log, message);
            }
        }

        return dangling;
    }



    /// <summary>Reconciles sidecars, then builds; reconcile first because rebuild-now does not wait out the debounce, and a wipe of every <c>.meta</c> would otherwise sit unnoticed until the next asset save.</summary>
    public BuildResult Rebuild(string? profile, ProjectOutputTarget target, ITextureEncoder? encoder)
    {
        _maintainer.Reconcile();
        ReconcileReferences();
        // One logger through, where this used to synthesise a second delegate that prefixed
        // "warning: " — the severity is BuildRunner's to state as a level now.
        return new BuildRunner(_fileSystem, _layout, encoder, _log, _importers)
            .Run(profile, target);
    }

    /// <summary>Records every asset's references where its importer keeps them (a mesh's sidecar) — a reconcile of references the way <see cref="SidecarMaintainer.Reconcile"/> is one of identities. Sidecars only; an asset's own bytes are followed on a rename (<see cref="Drain"/>), never under an author's feet at build time.</summary>
    public int ReconcileReferences()
    {
        if (_maintainer.DryRun)
        {
            LogWouldReconcile(_log);
            return 0;
        }

        var index = AssetIndex.Scan(_fileSystem, _layout.Assets, _maintainer.Ignore);
        var stamped = ReferenceRepair.Reconcile(_fileSystem, _layout, index, _importers);
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

    [LoggerMessage(EventId = 13, Level = LogLevel.Information, Message = "recorded: {Relative} ({Images} mesh reference(s) by identity)")]
    private static partial void LogStamped(ILogger logger, string relative, int images);

    [LoggerMessage(EventId = 14, Level = LogLevel.Information, Message = "rewrote: {Relative} ({Count} reference(s) caught up after a rename)")]
    private static partial void LogRewrote(ILogger logger, string relative, int count);

    [LoggerMessage(EventId = 15, Level = LogLevel.Information, Message = "deferred: {Relative} is being edited; its references are caught up on the next pass")]
    private static partial void LogDeferred(ILogger logger, string relative);

    [LoggerMessage(EventId = 16, Level = LogLevel.Information, Message = "would rewrite: {Relative} (dry run)")]
    private static partial void LogWouldRewrite(ILogger logger, string relative);

    [LoggerMessage(EventId = 17, Level = LogLevel.Warning, Message = "dangling: {Message}")]
    private static partial void LogDangling(ILogger logger, string message);

    [LoggerMessage(EventId = 18, Level = LogLevel.Information, Message = "would record references (dry run)")]
    private static partial void LogWouldReconcile(ILogger logger);

    [LoggerMessage(EventId = 19, Level = LogLevel.Information, Message = "not extracted: {Relative} — run `paradise assets extract {Relative}` to make its materials, textures and prefab")]
    private static partial void LogOffer(ILogger logger, string relative);

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "minted: {Written}")]
    private static partial void LogMinted(ILogger logger, string written);

    [LoggerMessage(EventId = 21, Level = LogLevel.Warning, Message = "not minted: {Error}")]
    private static partial void LogMintRefused(ILogger logger, string error);
}

/// <summary>What one <see cref="AssetWatcher.Drain"/> did.</summary>
/// <param name="Changes">Ripe events acted on: edits, adds, deletes and renames. Any of them
/// changes what a build would produce, so this is what the watch loop rebuilds on. Sidecar work
/// alone is not it: an asset that already has one reports zero sidecar actions on every edit, and
/// gating on that left content edits unbuilt (issue #195).</param>
/// <param name="SidecarActions">Sidecars minted, carried, quarantined, relinked or refreshed.</param>
/// <param name="Rewritten">Files whose references were caught up after an identity moved.</param>
/// <param name="Dangling">One line per reference left pointing at an identity whose delete just became final.</param>
public readonly record struct DrainResult(int Changes, int SidecarActions, int Rewritten, IReadOnlyList<string> Dangling);
