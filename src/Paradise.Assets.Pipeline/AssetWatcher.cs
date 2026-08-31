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
/// watching, so a mint fires a Created and a hash refresh fires a Changed, and without suppression
/// each one feeds itself forever. Paths the maintainer writes are held in a suppression set for a
/// moment afterwards and their events dropped — the same shape, and the same reason, as the Blender
/// addon's save suppression: a tool's own writes are not the author's.
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

    private readonly Lock _gate = new();
    private readonly Dictionary<UPath, DateTimeOffset> _pending = [];
    private readonly Dictionary<UPath, (UPath From, DateTimeOffset At)> _renames = [];
    private readonly Dictionary<UPath, DateTimeOffset> _suppressed = [];
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
        if (Ignored(path)) return;
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
    /// DELETES ARE HANDLED FIRST, and the order is the whole point: a move seen as delete-then-add
    /// only re-links if the identity is already in quarantine when the add is considered. Doing
    /// adds first would mint a new GUID and then quarantine the old one, which is the failure this
    /// is built to avoid, arrived at by scheduling.
    /// </remarks>
    public int Drain()
    {
        var now = _now();
        List<UPath> deletes;
        List<(UPath To, UPath From)> renames;
        List<UPath> touched;

        lock (_gate)
        {
            Forget(_suppressed, now, Debounce);

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
            if (Act(() => _maintainer.Carry(from, to), from, to)) actions++;
        }

        foreach (var path in touched)
        {
            if (Act(() => _maintainer.Ensure(path), path)) actions++;
        }

        // Only what a move could plausibly still be. Expiry forgets the chance to re-link; it
        // never removes a sidecar, so a real delete just leaves an orphan for `verify` to report.
        _maintainer.Expire(held => now - held.At > QuarantineWindow);
        return actions;
    }

    /// <summary>Runs one build, so the mounted tree matches what was just fixed.</summary>
    public BuildResult Rebuild(string profile, ProjectOutputTarget target, ITextureEncoder? encoder)
        => new BuildRunner(_fileSystem, _layout, encoder, _log, message => _log($"warning: {message}"))
            .Run(profile, target);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    /// <summary>Whether an event is one to act on at all.</summary>
    private bool Ignored(UPath path)
    {
        // A sidecar's own events are never ours. Most of them ARE ours -- the maintainer just
        // wrote it -- and the rest are the author editing an identity by hand, which is theirs to
        // do. Either way, acting on one is how the watcher would chase its own tail.
        if (SidecarMeta.IsSidecarPath(path)) return true;

        lock (_gate)
        {
            if (_suppressed.ContainsKey(path)) return true;
        }

        return false;
    }

    /// <summary>Runs a maintainer call and suppresses the sidecar writes it produces.</summary>
    private bool Act(Func<SidecarAction> action, params UPath[] assets)
    {
        // Suppress BEFORE the write, not after: the watcher is another thread, and an event
        // queued between the write and the suppression would be seen as the author's.
        var now = _now();
        lock (_gate)
        {
            foreach (var asset in assets) _suppressed[SidecarMeta.PathFor(asset)] = now;
        }

        return action() != SidecarAction.None;
    }


    /// <summary>Takes everything quiet long enough, removing it from the queue.</summary>
    private static List<UPath> Ripe(Dictionary<UPath, DateTimeOffset> queue, DateTimeOffset now)
    {
        var ripe = queue.Where(entry => now - entry.Value >= Debounce).Select(entry => entry.Key).ToList();
        foreach (var path in ripe) queue.Remove(path);
        return ripe;
    }

    private static void Forget(Dictionary<UPath, DateTimeOffset> queue, DateTimeOffset now, TimeSpan after)
    {
        foreach (var path in queue.Where(entry => now - entry.Value > after).Select(entry => entry.Key).ToList())
        {
            queue.Remove(path);
        }
    }
}
