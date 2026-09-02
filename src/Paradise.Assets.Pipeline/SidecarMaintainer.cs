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
}

/// <summary>Keeps <c>*.meta</c> in step with the assets beside them; holds the rules and none of the timing, so each is testable without a clock.</summary>
/// <remarks>
/// Nothing here may destroy an identity: a deleted <c>.meta</c> breaks every reference, and most
/// moves (<c>git mv</c> on Windows, Finder) arrive as delete-then-add. So a delete quarantines,
/// and an asset reappearing with the same content takes the identity back. The match is on a hash
/// held in memory, never a field in the sidecar, because a recorded hash of a text asset differs
/// per checkout (line endings, smudge filters) and would make every clone a dirty tree. The one
/// exception is <see cref="Carry"/>, which overwrites an existing destination — issue #196.
/// </remarks>
public sealed class SidecarMaintainer
{
    private readonly IFileSystem _fileSystem;
    private readonly AssetProjectLayout _layout;
    private readonly Action<string> _log;
    private readonly bool _dryRun;

    private readonly Dictionary<string, QuarantinedIdentity> _quarantine = [];

    private readonly Dictionary<UPath, string> _seenHash = [];

    public SidecarMaintainer(
        IFileSystem fileSystem,
        AssetProjectLayout layout,
        Action<string>? log = null,
        bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);

        _fileSystem = fileSystem;
        _layout = layout;
        _log = log ?? (static _ => { });
        _dryRun = dryRun;
    }

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
        if (!AssetClassifier.NeedsSidecar(AssetClassifier.Classify(_layout.Assets, asset))
            || !_fileSystem.FileExists(asset))
        {
            return SidecarAction.None;
        }

        var sidecar = SidecarMeta.PathFor(asset);
        var hash = SidecarMeta.ComputeHash(_fileSystem, asset);
        _seenHash[asset] = hash;

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
                _log($"{Display(sidecar)}: left alone — {error.Message}");
                return SidecarAction.None;
            }

            if (existing.Hash is null) return SidecarAction.None;

            existing.Hash = null;
            Save(existing, sidecar);
            _log($"refreshed: {Display(sidecar)} (dropped recorded hash)");
            return SidecarAction.Refreshed;
        }

        if (_quarantine.Remove(hash, out var held))
        {
            var restored = new SidecarMeta(held.Meta.Guid);
            foreach (var (domain, settings) in held.Meta.Settings) restored.SetSetting(domain, settings);
            Save(restored, sidecar);
            Remove(held.Sidecar);
            _seenHash.Remove(held.Asset);
            _log($"relinked: {Display(held.Asset)} -> {Display(asset)} (guid kept)");
            return SidecarAction.Relinked;
        }

        var minted = new SidecarMeta(Guid.NewGuid());
        Save(minted, sidecar);
        _log($"minted: {Display(sidecar)}");
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

        if (_seenHash.Remove(from, out var remembered)) _seenHash[to] = remembered;

        meta.Hash = null;
        Save(meta, SidecarMeta.PathFor(to));
        Remove(source);
        _log($"carried: {Display(source)} -> {Display(SidecarMeta.PathFor(to))}");
        return SidecarAction.Carried;
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

        if (!_seenHash.Remove(asset, out var hash)) hash = meta.Hash;
        if (hash is null) return SidecarAction.None;

        _quarantine[hash] = new QuarantinedIdentity(asset, sidecar, meta, at);
        _log($"quarantined: {Display(sidecar)} (identity held in case this is a move)");
        return SidecarAction.Quarantined;
    }

    /// <summary>Forgets held identities; the sidecar stays on disk, so this only drops the chance to re-link.</summary>
    public void Expire(Func<QuarantinedIdentity, bool> stale)
    {
        ArgumentNullException.ThrowIfNull(stale);
        foreach (var (hash, held) in _quarantine.Where(entry => stale(entry.Value)).ToList())
        {
            _quarantine.Remove(hash);
            _log($"orphaned: {Display(held.Sidecar)} — no asset reappeared with its content");
        }
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

    private string Display(UPath path)
    {
        var full = path.FullName;
        var root = _layout.Assets.FullName;
        return full.StartsWith(root, StringComparison.Ordinal) ? full[(root.Length + 1)..] : full;
    }
}

public readonly record struct QuarantinedIdentity(UPath Asset, UPath Sidecar, SidecarMeta Meta, DateTimeOffset At);
