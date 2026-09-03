using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

public enum SidecarAction
{
    None,

    Minted,

    /// <summary>A legacy recorded hash was dropped.</summary>
    Refreshed,

    Carried,

    /// <summary>A deleted asset's identity was held in case the delete was half of a move.</summary>
    Quarantined,

    Relinked,

    /// <summary>A sidecar found beside an ignored file was deleted: minted before the file was ignored, it would be committed while the file it describes is gitignored.</summary>
    Removed,

    /// <summary>A rename landed on a path that already had a sidecar; that identity was kept and the arriving one dropped.</summary>
    Conflicted,
}

/// <summary>Keeps <c>*.meta</c> in step with the assets beside them; holds the rules and none of the timing, so each is testable without a clock.</summary>
/// <remarks>
/// Nothing here may destroy an identity: a deleted <c>.meta</c> breaks every reference, and most
/// moves (<c>git mv</c> on Windows, Finder) arrive as delete-then-add. So a delete quarantines,
/// and an asset reappearing with the same content takes the identity back. The match is on a hash
/// held in memory, never a field in the sidecar, because a recorded hash of a text asset differs
/// per checkout (line endings, smudge filters) and would make every clone a dirty tree.
/// </remarks>
public sealed partial class SidecarMaintainer
{
    private readonly IFileSystem _fileSystem;
    private readonly AssetProjectLayout _layout;
    private readonly ILogger _log;
    private readonly bool _dryRun;
    private readonly AssetIgnoreRules _ignore;

    private readonly Dictionary<string, QuarantinedIdentity> _quarantine = [];

    // Hashed once per (mtime, size): every watch rebuild reconciles first, and hashing the
    // whole tree each time was most of what a rebuild cost (#203).
    private readonly Dictionary<UPath, SeenAsset> _seen = [];

    /// <param name="ignore">The project's <c>[assets] ignore</c>; taken once, so a change to it needs the watch restarted.</param>
    public SidecarMaintainer(
        IFileSystem fileSystem,
        AssetProjectLayout layout,
        ILogger? logger = null,
        bool dryRun = false,
        AssetIgnoreRules? ignore = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);

        _fileSystem = fileSystem;
        _layout = layout;
        _log = logger ?? NullLogger.Instance;
        _dryRun = dryRun;
        _ignore = ignore ?? AssetIgnoreRules.None;
    }

    public AssetIgnoreRules Ignore => _ignore;

    public IReadOnlyCollection<string> Quarantined => _quarantine.Keys;

    /// <summary>Brings every asset under <c>assets/</c> into line and returns how many it touched.</summary>
    public int Reconcile()
    {
        if (!_fileSystem.DirectoryExists(_layout.Assets)) return 0;

        var touched = 0;
        foreach (var path in _fileSystem
            .EnumerateFiles(_layout.Assets, "*", SearchOption.AllDirectories)
            .OrderBy(p => p.FullName, StringComparer.Ordinal))
        {
            if (SidecarMeta.IsSidecarPath(path)) continue;
            if (Ensure(path) != SidecarAction.None) touched++;
        }

        return touched;
    }

    /// <summary>Gives an asset a sidecar, or brings the one it has up to date; add and change are one method because a temp-then-rename save arrives as either.</summary>
    public SidecarAction Ensure(UPath asset)
    {
        if (_ignore.Matches(_layout.Assets, asset)) return RemoveIgnoredSidecar(asset);

        if (!AssetClassifier.NeedsSidecar(AssetClassifier.Classify(_layout.Assets, asset, _ignore))
            || !_fileSystem.FileExists(asset))
        {
            return SidecarAction.None;
        }

        var sidecar = SidecarMeta.PathFor(asset);
        var hash = HashOf(asset);

        if (_fileSystem.FileExists(sidecar))
        {
            SidecarMeta existing;
            try
            {
                existing = SidecarMeta.Load(_fileSystem, sidecar);
            }
            catch (SidecarMetaException error)
            {
                // Left alone: it may hold the only copy of an identity.
                LogLeftAlone(_log, sidecar, error.Message);
                return SidecarAction.None;
            }

            if (existing.Hash is null) return SidecarAction.None;

            existing.Hash = null;
            Save(existing, sidecar);
            LogRefreshed(_log, sidecar);
            return SidecarAction.Refreshed;
        }

        if (_quarantine.Remove(hash, out var held))
        {
            var restored = new SidecarMeta(held.Meta.Guid);
            foreach (var (domain, settings) in held.Meta.Settings) restored.SetSetting(domain, settings);
            Save(restored, sidecar);
            Remove(held.Sidecar);
            _seen.Remove(held.Asset);
            LogRelinked(_log, held.Asset, asset);
            return SidecarAction.Relinked;
        }

        var minted = new SidecarMeta(Guid.NewGuid());
        Save(minted, sidecar);
        LogMinted(_log, sidecar);
        return SidecarAction.Minted;
    }

    public SidecarAction Carry(UPath from, UPath to)
    {
        var source = SidecarMeta.PathFor(from);
        if (!_fileSystem.FileExists(source)) return Ensure(to);

        SidecarMeta meta;
        try
        {
            meta = SidecarMeta.Load(_fileSystem, source);
        }
        catch (SidecarMetaException)
        {
            return SidecarAction.None;
        }

        if (_seen.Remove(from, out var remembered)) _seen[to] = remembered;

        var destination = SidecarMeta.PathFor(to);
        if (_fileSystem.FileExists(destination))
        {
            // The destination's identity is the one every reference to this path already names.
            // The arriving one is almost always a temp file's mint that outlived the debounce;
            // overwriting would break every reference at once (issue #196). Both guids go to the
            // log so an author can settle the rare case where the arriving one was the real one.
            Remove(source);
            LogKept(
                _log,
                destination,
                DocumentGuid.Format(Existing(destination) ?? Guid.Empty),
                source,
                DocumentGuid.Format(meta.Guid));
            return SidecarAction.Conflicted;
        }

        meta.Hash = null;
        Save(meta, destination);
        Remove(source);
        LogCarried(_log, source, destination);
        return SidecarAction.Carried;
    }

    private Guid? Existing(UPath sidecar)
    {
        try
        {
            return SidecarMeta.Load(_fileSystem, sidecar).Guid;
        }
        catch (SidecarMetaException)
        {
            return null;
        }
    }

    public SidecarAction Quarantine(UPath asset, DateTimeOffset at)
    {
        var sidecar = SidecarMeta.PathFor(asset);
        if (!_fileSystem.FileExists(sidecar)) return SidecarAction.None;

        SidecarMeta meta;
        try
        {
            meta = SidecarMeta.Load(_fileSystem, sidecar);
        }
        catch (SidecarMetaException)
        {
            return SidecarAction.None;
        }

        var hash = _seen.Remove(asset, out var seen) ? seen.Hash : meta.Hash;
        if (hash is null) return SidecarAction.None;

        _quarantine[hash] = new QuarantinedIdentity(asset, sidecar, meta, at);
        LogQuarantined(_log, sidecar);
        return SidecarAction.Quarantined;
    }

    /// <summary>Forgets held identities; the sidecar stays on disk, so this only drops the chance to re-link.</summary>
    public void Expire(Func<QuarantinedIdentity, bool> stale)
    {
        ArgumentNullException.ThrowIfNull(stale);
        foreach (var (hash, held) in _quarantine.Where(entry => stale(entry.Value)).ToList())
        {
            _quarantine.Remove(hash);
            LogOrphaned(_log, held.Sidecar);
        }
    }

    /// <summary>The one identity this class may destroy: nothing can reference a file the pipeline never builds.</summary>
    private SidecarAction RemoveIgnoredSidecar(UPath asset)
    {
        var sidecar = SidecarMeta.PathFor(asset);
        if (!_fileSystem.FileExists(sidecar)) return SidecarAction.None;

        Remove(sidecar);
        LogRemovedIgnored(_log, sidecar);
        return SidecarAction.Removed;
    }

    private string HashOf(UPath asset)
    {
        var stamp = FileStamp.Of(_fileSystem, asset);
        if (stamp is { } current && _seen.TryGetValue(asset, out var seen) && seen.Stamp == current) return seen.Hash;

        var hash = SidecarMeta.ComputeHash(_fileSystem, asset);
        if (stamp is { } taken) _seen[asset] = new SeenAsset(taken, hash);
        else _seen.Remove(asset);
        return hash;
    }

    private void Save(SidecarMeta meta, UPath path)
    {
        if (_dryRun) return;
        meta.Save(_fileSystem, path);
    }

    private void Remove(UPath path)
    {
        if (_dryRun || !_fileSystem.FileExists(path)) return;
        _fileSystem.DeleteFile(path);
    }

    // Every path below is logged as a UPath, NOT as a pre-rendered string. This class used to
    // hold a `Display` helper that trimmed the assets root off the front, which is one host's
    // preference baked into the layer that cannot know it: a watch console wants
    // `props/lamp.glb`, an editor's problem list wants something clickable, and neither is
    // reachable from a string that has already been shortened. The value goes out intact and
    // ParadiseConsoleOptions.RenderValue decides — see issue #232.
    //
    // [LoggerMessage] rather than _log.LogInformation: the generator emits the IsEnabled check
    // before touching an argument, so a host that filters these out pays no boxing for the UPath.

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "{Sidecar}: left alone — {Reason}")]
    private static partial void LogLeftAlone(ILogger logger, UPath sidecar, string reason);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "refreshed: {Sidecar} (dropped recorded hash)")]
    private static partial void LogRefreshed(ILogger logger, UPath sidecar);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "relinked: {From} -> {To} (guid kept)")]
    private static partial void LogRelinked(ILogger logger, UPath from, UPath to);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "minted: {Sidecar}")]
    private static partial void LogMinted(ILogger logger, UPath sidecar);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "kept: {Destination} already holds {KeptGuid}; dropped {Source} ({DroppedGuid}) arriving by rename")]
    private static partial void LogKept(ILogger logger, UPath destination, string keptGuid, UPath source, string droppedGuid);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "carried: {Source} -> {Destination}")]
    private static partial void LogCarried(ILogger logger, UPath source, UPath destination);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "quarantined: {Sidecar} (identity held in case this is a move)")]
    private static partial void LogQuarantined(ILogger logger, UPath sidecar);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "orphaned: {Sidecar} — no asset reappeared with its content")]
    private static partial void LogOrphaned(ILogger logger, UPath sidecar);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information, Message = "removed: {Sidecar} (its asset is in [assets] ignore)")]
    private static partial void LogRemovedIgnored(ILogger logger, UPath sidecar);
}

public readonly record struct QuarantinedIdentity(UPath Asset, UPath Sidecar, SidecarMeta Meta, DateTimeOffset At);

internal readonly record struct SeenAsset((long Mtime, long Size) Stamp, string Hash);
