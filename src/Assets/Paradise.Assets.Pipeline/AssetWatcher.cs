using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>Maintains sidecars and triggers incremental builds after asset changes.</summary>
/// <remarks>
/// <see cref="SidecarMaintainer"/> owns identity rules; this class adds event timing and debounce.
/// Pipeline writes update the live inventory immediately; matching OS echoes are ignored.
/// Startup, directory events, lost events, explicit full rebuilds and manifest changes trigger full recovery.
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

    private readonly HashSet<UPath> _written = [];
    private readonly HashSet<UPath> _dirty = [];
    private readonly Dictionary<UPath, (long Mtime, long Size)?> _ownedWrites = [];
    private AssetIndex? _index;
    private ReferenceGraph? _graph;
    private BuildRunner? _runner;
    private ITextureEncoder? _encoder;
    private bool _recover;
    private bool _fullBuild = true;
    private (long Mtime, long Size)? _manifestStamp;

    private IFileSystemWatcher? _watcher;

    /// <summary>Creates a watcher over one project; <paramref name="importers"/> is the chain every rebuild runs, and the same chain says what a source container is (the built-ins when omitted).</summary>
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

        _fileSystem = new WatchFileSystem(fileSystem, RecordWrite);
        _layout = layout;
        _maintainer = maintainer;
        _log = logger ?? NullLogger.Instance;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _importers = importers ?? AssetImporters.All;
        _maintainer.Changed += RecordWrite;
    }

    /// <summary>Whether an event is queued or full recovery was requested.</summary>
    public bool HasPending
    {
        get { lock (_gate) { return _recover || _pending.Count > 0 || _deleted.Count > 0 || _renames.Count > 0; } }
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
        _watcher.Error += (_, e) =>
        {
            Invalidate();
            LogWatcherFaulted(_log, e.Exception.Message);
        };
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Records an add or a change. Public so tests can drive it without a disk.</summary>
    public void Observe(UPath path)
    {
        if (!path.IsInDirectory(_layout.Assets, recursive: true)) return;
        lock (_gate) { _pending[path] = _now(); }
    }

    /// <summary>Records a delete.</summary>
    public void ObserveDelete(UPath path)
    {
        if (!path.IsInDirectory(_layout.Assets, recursive: true)) return;
        lock (_gate) { _deleted[path] = _now(); }
    }

    /// <summary>Records a rename, which is the only move that announces itself as one.</summary>
    public void ObserveRename(UPath from, UPath to)
    {
        if (!from.IsInDirectory(_layout.Assets, recursive: true) && !to.IsInDirectory(_layout.Assets, recursive: true)) return;
        lock (_gate) { _renames[to] = (from, _now()); }
    }

    /// <summary>Requests full inventory and reference recovery after filesystem events were lost.</summary>
    public void Invalidate()
    {
        lock (_gate) { _recover = true; }
    }

    /// <summary>Acts on everything quiet for <see cref="Debounce"/>.</summary>
    /// <remarks>
    /// Deletes before adds, or a move seen as delete-then-add would mint a new GUID before the old
    /// one reached quarantine. The maintainer runs outside the lock (its work is IO) and keeps its
    /// quarantine unsynchronized, so callers sharing this watcher or maintainer must not overlap drains.
    /// </remarks>
    public DrainResult Drain()
    {
        var now = _now();
        List<UPath> deletes;
        List<(UPath To, UPath From)> renames;
        List<UPath> touched;
        bool recover;
        lock (_gate)
        {
            deletes = Ripe(_deleted, now);
            renames = [.. Ripe(_renames.ToDictionary(e => e.Key, e => e.Value.At), now)
                .Select(to => (to, _renames[to].From))];
            foreach (var (to, _) in renames) _renames.Remove(to);
            touched = Ripe(_pending, now);
            recover = _recover;
            _recover = false;
        }

        deletes.RemoveAll(IsOwnedEcho);
        touched.RemoveAll(IsOwnedEcho);
        renames.RemoveAll(pair => IsOwnedEcho(pair.From) && IsOwnedEcho(pair.To));
        recover |= touched.Contains(_layout.Manifest) || deletes.Contains(_layout.Manifest)
            || renames.Any(pair => pair.From == _layout.Manifest || pair.To == _layout.Manifest);
        EnsureInventory();
        recover |= touched.Any(_fileSystem.DirectoryExists)
            || renames.Any(pair => _fileSystem.DirectoryExists(pair.To) || HasChildren(pair.From))
            || deletes.Any(HasChildren);
        var changes = deletes.Count + renames.Count + touched.Count;
        if (recover)
        {
            // Directory and manifest triggers leave this batch's file events intact; the rescan
            // only re-ensures sidecars, so held and carried identities must be settled first.
            var (recovered, followed) = Recover(PreserveIdentities(deletes, renames, now));
            var expiredDuringRecovery = _maintainer.Expire(held => now - held.At > QuarantineWindow);
            IReadOnlyList<string> danglingAfterRecovery = expiredDuringRecovery.Count > 0 ? ReportDangling(expiredDuringRecovery) : [];
            return new DrainResult(Math.Max(1, changes) + followed, recovered, followed, danglingAfterRecovery);
        }

        FindCaseRenames(touched, deletes, renames);

        var affected = new HashSet<UPath>();
        var actions = 0;
        var carried = new List<UPath>();
        foreach (var path in deletes)
        {
            CollectAffected(path, affected);
            if (SidecarMeta.IsSidecarPath(path))
            {
                var owner = SidecarMeta.AssetPathFor(path);
                if (_maintainer.Ensure(owner) != SidecarAction.None) actions++;
            }
            else if (!_fileSystem.FileExists(path) && _maintainer.Quarantine(path, now) != SidecarAction.None)
            {
                actions++;
            }
            Refresh(path, affected);
        }

        foreach (var (to, from) in renames)
        {
            CollectAffected(from, affected);
            if (_maintainer.Ignore.Matches(_layout.Assets, to))
            {
                // Blender renames the previous save to .blend1 before replacing .blend.
                // The live path keeps its identity; a backup never inherits its sidecar.
                if (!_fileSystem.FileExists(from)) _maintainer.Quarantine(from, now);
                Refresh(from, affected);
                continue;
            }
            if (!SidecarMeta.IsSidecarPath(from) && !SidecarMeta.IsSidecarPath(to))
            {
                var action = _maintainer.Carry(from, to);
                if (action != SidecarAction.None) actions++;
                if (action is SidecarAction.Carried or SidecarAction.Relinked) carried.Add(to);
                CarryConverted(from, to);
            }
            _index!.Remove(from);
            if (!SidecarMeta.IsSidecarPath(from)) _index.Remove(SidecarMeta.PathFor(from));
            _graph!.Remove(from);
            _dirty.Add(from);
            _dirty.Add(SidecarMeta.PathFor(from));
            affected.Remove(from);
            ForgetOldWrite(from);
            ForgetOldWrite(SidecarMeta.PathFor(from));
            Refresh(to, affected);
        }

        foreach (var path in touched)
        {
            var owner = Owner(path);
            var action = _maintainer.Ensure(owner);
            if (action != SidecarAction.None) actions++;
            if (action == SidecarAction.Relinked) carried.Add(owner);
            Refresh(path, affected);
        }
        FlushWrites(affected);

        foreach (var path in affected.ToArray())
        {
            if (!Pending(path)) actions += MintReferences(path);
        }
        FlushWrites(affected);
        ReconcileReferences(affected);
        FlushWrites(affected);
        var rewritten = carried.Count > 0 ? FollowRenames(carried) : 0;
        rewritten += RetryDeferred();
        FlushWrites(affected);
        _dirty.UnionWith(affected);
        var expired = _maintainer.Expire(held => now - held.At > QuarantineWindow);
        IReadOnlyList<string> dangling = expired.Count > 0 ? ReportDangling(expired) : [];
        return new DrainResult(changes + rewritten, actions, rewritten, dangling);
    }

    private List<UPath> PreserveIdentities(List<UPath> deletes, List<(UPath To, UPath From)> renames, DateTimeOffset now)
    {
        foreach (var path in deletes)
        {
            if (!SidecarMeta.IsSidecarPath(path) && !_fileSystem.FileExists(path)) _maintainer.Quarantine(path, now);
        }

        var carried = new List<UPath>();
        foreach (var (to, from) in renames)
        {
            if (SidecarMeta.IsSidecarPath(from) || SidecarMeta.IsSidecarPath(to)) continue;
            if (_maintainer.Ignore.Matches(_layout.Assets, to))
            {
                if (!_fileSystem.FileExists(from)) _maintainer.Quarantine(from, now);
                continue;
            }
            // A moved directory's files travel with their sidecars; only its converted GLBs need carrying.
            if (!_fileSystem.DirectoryExists(to) && _maintainer.Carry(from, to) is SidecarAction.Carried or SidecarAction.Relinked) carried.Add(to);
            CarryConverted(from, to);
        }
        return carried;
    }

    /// <summary>
    /// After an identity moved, every file that references it has its path half (a document's
    /// reference, the path a model source's sidecar records for a file it names) caught up, so a
    /// rename done in Finder leaves the tree as tidy as <c>mv</c> would; a model source itself is
    /// never written. Only the dependents, through the graph — and not one that is itself
    /// mid-edit (still pending its debounce): it is rewritten on the next drain instead.
    /// </summary>
    /// <remarks>Dependents still being edited are deferred; dry-run reports without writing.</remarks>
    private int FollowRenames(IReadOnlyList<UPath> carried)
    {
        var index = _index!;
        var graph = _graph!;

        // The carried assets themselves too: an unrecorded uri in a model source is relative to it.
        var dependents = new List<UPath>(carried);
        foreach (var path in carried)
        {
            if (index.IdentityOf(path) is { } guid) dependents.AddRange(graph.DependentFilesOf(guid));
        }

        return CatchUp(index, dependents.Distinct());
    }

    /// <summary>Every source container's tool-owned documents, for the watch verb's start: the tree the way a drain would leave it, before the first save.</summary>
    /// <remarks>One inventory scan initializes the session; generated changes update that inventory in place.</remarks>
    /// <param name="cancellation">Checked between sources, so a stop asked for during a long start is not held until every source is done; a source already started finishes, since its writes are one unit.</param>
    public int MintReferences(CancellationToken cancellation = default) => Recover([], cancellation).Minted;

    /// <summary>Whether the chain reads this file as a source container — its sidecar's importer, else the claim.</summary>
    private bool Extractable(UPath path) => ImporterChain.Extractor(_importers, _fileSystem, _layout, path) is not null;

    /// <summary>
    /// A source container gets its tool-owned documents on the spot: they carry no author work,
    /// and a re-export that adds a clip should add its document without a verb. Materials,
    /// textures and the prefab are the author's from the moment they exist, so those are offered,
    /// never written — extraction of them mints files an author edits, which is not a watcher's to
    /// do on a save.
    /// </summary>
    private int MintReferences(UPath path)
    {
        if (SidecarMeta.IsSidecarPath(path) || _index!.IsIgnored(path)) return 0;
        if (ImporterChain.Extractor(_importers, _fileSystem, _layout, path) is not { } extractor || !_fileSystem.FileExists(path)) return 0;
        var sidecar = SidecarMeta.PathFor(path);
        // Empty containers still need reconciliation when their last model was removed.
        if (!_fileSystem.FileExists(sidecar)) return 0;

        var relative = path.FullName[(_layout.Assets.FullName.Length + 1)..];
        if (_maintainer.DryRun)
        {
            LogWouldMint(_log, relative);
            return 0;
        }

        var result = extractor.MintReferences(new ExtractRequest(_fileSystem, _layout, path, _importers, Logger: _log, Maintainer: _maintainer, Index: _index, References: _graph));
        FlushWrites();
        foreach (var error in result.Errors) LogMintRefused(_log, error);
        foreach (var written in result.Written) LogMinted(_log, written.ToString());

        if (result.HasAuthoredParts && !extractor.IsExtracted(_fileSystem, path)) LogOffer(_log, relative);

        return result.Written.Count;
    }

    /// <summary>Whatever an earlier pass deferred and is quiet now.</summary>
    private int RetryDeferred()
    {
        if (_deferred.Count == 0) return 0;
        return CatchUp(_index!, [.. _deferred]);
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
                // A model source's catch-up only ever writes its sidecar.
                LogWouldRewrite(_log, index.Relative(ModelSource.IsModel(path) ? SidecarMeta.PathFor(path) : path));
                continue;
            }

            var context = new ReferenceContext(_fileSystem, _layout, index, _maintainer.Ignore);
            if (ReferenceChain.Rewrite(_importers, context, path) is not { } repaired) continue;
            rewritten++;
            LogRewrote(_log, index.Relative(repaired.Path), repaired.Repointed.Count);
        }

        return rewritten;
    }

    /// <summary>What a delete that really was one left dangling: the graph still holds every edge into the identity that is gone.</summary>
    private List<string> ReportDangling(IReadOnlyList<QuarantinedIdentity> expired)
    {
        var index = _index!;
        var graph = _graph!;

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

    /// <summary>Builds recorded changes, or fully reconciles when explicitly requested or the inventory is invalid.</summary>
    public BuildResult Rebuild(string? profile, ProjectOutputTarget target, ITextureEncoder? encoder, Action<BuildProgress>? progress = null, bool full = false)
    {
        progress?.Invoke(new BuildProgress(BuildStage.Sidecars, 0, 0, null));
        lock (_gate)
        {
            full |= _recover;
            _recover = false;
        }
        if (full || _index is null || _manifestStamp != FileStamp.Of(_fileSystem, _layout.Manifest)) Recover([]);
        if (_runner is null || !ReferenceEquals(_encoder, encoder))
        {
            _encoder = encoder;
            _runner = new BuildRunner(_fileSystem, _layout, encoder, _log, _importers);
        }
        var result = _runner.Run(profile, target, progress, _index, _fullBuild ? null : _dirty);
        FlushWrites();
        if (result.Succeeded)
        {
            _dirty.Clear();
            _fullBuild = false;
        }
        return result;
    }

    private AssetIgnoreRules ManifestIgnore()
    {
        try
        {
            return ProjectManifest.Load(_fileSystem, _layout.Manifest).Ignore;
        }
        catch (ProjectManifestException)
        {
            return _maintainer.Ignore;
        }
    }

    /// <summary>
    /// A converted GLB is found by its source's path, so a rename made outside <c>mv</c> takes it
    /// along as <c>mv</c> does: left behind, it is orphaned, and the renamed source is converted
    /// again — or cannot be read at all on a machine without Blender. Blender's own save renames
    /// <c>x.blend@</c> over <c>x.blend</c>, which is no model source moving and is left alone.
    /// </summary>
    private void CarryConverted(UPath from, UPath to)
    {
        if (_maintainer.DryRun) return;
        try
        {
            AssetMover.MoveConverted(_fileSystem, _layout, from, to, isDirectory: _fileSystem.DirectoryExists(to));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LogConvertedStayed(_log, to.FullName, error.Message);
        }
    }

    /// <summary>Records reference sidecars throughout the current inventory without another scan.</summary>
    public int ReconcileReferences()
    {
        EnsureInventory();
        var count = ReconcileReferences(_index!.Files.ToArray());
        FlushWrites();
        return count;
    }

    private int ReconcileReferences(IEnumerable<UPath> paths)
    {
        if (_maintainer.DryRun)
        {
            LogWouldReconcile(_log);
            return 0;
        }
        var context = new ReferenceContext(_fileSystem, _layout, _index!, _maintainer.Ignore, RewriteSources: false);
        var count = 0;
        foreach (var path in paths.Select(Owner).Distinct())
        {
            if (!_fileSystem.FileExists(path) || _index!.IsIgnored(path) || Pending(path)) continue;
            if (ReferenceChain.Rewrite(_importers, context, path) is not { } stamped) continue;
            LogStamped(_log, _index.Relative(stamped.Path), stamped.Repointed.Count);
            count++;
        }
        return count;
    }

    private void EnsureInventory()
    {
        if (_index is not null) return;
        _index = AssetIndex.Scan(_fileSystem, _layout.Assets, _maintainer.Ignore);
        _graph = ReferenceGraph.Build(_fileSystem, _layout, _index, _maintainer.Ignore, _importers);
    }

    /// <summary>Rescans the inventory and reconciles identities, generated documents and references.</summary>
    /// <param name="carried">Paths whose identity moved before the rescan; their dependents' path hints are caught up afterwards.</param>
    private (int Minted, int Rewritten) Recover(IReadOnlyList<UPath> carried, CancellationToken cancellation = default)
    {
        _fullBuild = true;
        _maintainer.SetIgnore(ManifestIgnore());
        _manifestStamp = FileStamp.Of(_fileSystem, _layout.Manifest);
        _index = AssetIndex.Scan(_fileSystem, _layout.Assets, _maintainer.Ignore);
        _graph = null;
        var moved = new List<UPath>(carried);
        foreach (var path in _index.Files.ToArray())
        {
            if (!SidecarMeta.IsSidecarPath(path) && _maintainer.Ensure(path) == SidecarAction.Relinked) moved.Add(path);
        }
        FlushWrites();
        _graph = ReferenceGraph.Build(_fileSystem, _layout, _index, _maintainer.Ignore, _importers);
        var sources = _index.Files.Where(path => !SidecarMeta.IsSidecarPath(path) && !_index.IsIgnored(path) && Extractable(path)).ToArray();
        var minted = 0;
        foreach (var path in sources)
        {
            if (cancellation.IsCancellationRequested) break;
            minted += MintReferences(path);
        }
        ReconcileReferences(_index.Files.ToArray());
        FlushWrites();
        // Recovery reconciles only source-owned records; authored documents follow moves as a drain's would.
        var rewritten = moved.Count > 0 ? FollowRenames(moved) : 0;
        rewritten += RetryDeferred();
        FlushWrites();
        return (minted, rewritten);
    }

    private static UPath Owner(UPath path) => SidecarMeta.IsSidecarPath(path) ? SidecarMeta.AssetPathFor(path) : path;

    private bool Pending(UPath path)
    {
        lock (_gate) { return _pending.ContainsKey(path) || _pending.ContainsKey(SidecarMeta.PathFor(path)); }
    }

    // An indexed file cannot also be a directory, so ordinary file events skip the inventory scan.
    private bool HasChildren(UPath path) => !_index!.Contains(path) && _index.Files.Any(file => file.IsInDirectory(path, recursive: true));

    private void FindCaseRenames(List<UPath> touched, List<UPath> deletes, List<(UPath To, UPath From)> renames)
    {
        for (var i = 0; i < touched.Count; i++)
        {
            var path = touched[i];
            if (_index!.Contains(path) || !_index.TryFindIgnoringCase(path, out var previous)
                || !_fileSystem.FileExists(path)) continue;

            // APFS can announce a case-only rename as two creates. Probe only that directory
            // to distinguish a moved spelling from two genuine files on a case-sensitive mount.
            var hasPrevious = false;
            var hasCurrent = false;
            foreach (var candidate in _fileSystem.EnumerateFiles(path.GetDirectory()))
            {
                hasPrevious |= candidate == previous;
                hasCurrent |= candidate == path;
            }

            if (hasPrevious)
            {
                if (!hasCurrent) touched[i] = previous;
            }
            else if (hasCurrent && !renames.Contains((path, previous)))
            {
                renames.Add((path, previous));
            }
        }

        foreach (var (to, from) in renames)
        {
            if (from == to || !string.Equals(from.FullName, to.FullName, StringComparison.OrdinalIgnoreCase)) continue;
            // FileExists still accepts the old spelling; do not restore it from this batch.
            touched.Remove(from);
            touched.Remove(to);
            touched.Remove(SidecarMeta.PathFor(from));
            deletes.Remove(from);
            deletes.Remove(SidecarMeta.PathFor(from));
        }
    }

    private void CollectAffected(UPath path, HashSet<UPath> affected)
    {
        path = Owner(path);
        if (!affected.Add(path) || _graph is null) return;
        if (_index!.IdentityOf(path) is { } guid)
        {
            foreach (var edge in _graph.DependentsOf(guid)) CollectAffected(edge.ReferrerPath, affected);
        }
        var relative = _index.Relative(path);
        foreach (var (asset, site) in _graph.PathOnly)
        {
            if (site.Hint == relative) CollectAffected(asset, affected);
        }
    }

    private void Refresh(UPath path, HashSet<UPath>? affected = null)
    {
        _dirty.Add(path);
        var owner = Owner(path);
        _dirty.Add(owner);
        if (affected is not null) CollectAffected(owner, affected);
        _index!.Refresh(_fileSystem, path, _maintainer.Ignore);
        // A replaced sidecar can introduce a different GUID with its own incoming edges.
        if (_graph is not null && _index.IdentityOf(owner) is { } guid)
        {
            foreach (var edge in _graph.DependentsOf(guid))
            {
                _dirty.Add(edge.ReferrerPath);
                if (affected is not null) CollectAffected(edge.ReferrerPath, affected);
            }
        }
        _graph?.Refresh(_fileSystem, _layout, _index, owner, _maintainer.Ignore, _importers);
    }

    private void RecordWrite(UPath path)
    {
        if (path.IsInDirectory(_layout.Assets, recursive: true)) _written.Add(path);
    }

    private void ForgetOldWrite(UPath path)
    {
        _written.Remove(path);
        _ownedWrites[path] = FileStamp.Of(_fileSystem, path);
    }

    private void FlushWrites(HashSet<UPath>? affected = null)
    {
        if (_written.Count == 0) return;
        var paths = _written.ToArray();
        _written.Clear();
        foreach (var path in paths)
        {
            if (!path.IsInDirectory(_layout.Assets, recursive: true)) continue;
            _ownedWrites[path] = FileStamp.Of(_fileSystem, path);
            Refresh(path, affected);
        }
    }

    private bool IsOwnedEcho(UPath path)
    {
        if (!_ownedWrites.TryGetValue(path, out var stamp)) return false;
        if (FileStamp.Of(_fileSystem, path) == stamp) return true;
        _ownedWrites.Remove(path);
        return false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _maintainer.Changed -= RecordWrite;
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

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

    [LoggerMessage(EventId = 22, Level = LogLevel.Information, Message = "would mint the mesh, skeleton and clip documents of {Relative} (dry run)")]
    private static partial void LogWouldMint(ILogger logger, string relative);

    [LoggerMessage(EventId = 23, Level = LogLevel.Warning, Message = "the GLB converted before {Path} was renamed could not follow it ({Reason}); it is converted again on the next read")]
    private static partial void LogConvertedStayed(ILogger logger, string path, string reason);
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
