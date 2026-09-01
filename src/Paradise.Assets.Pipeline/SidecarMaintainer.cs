using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>What a maintainer call did, for a caller that wants to say so.</summary>
public enum SidecarAction
{
    /// <summary>Nothing needed doing.</summary>
    None,

    /// <summary>An asset with no sidecar got a fresh identity.</summary>
    Minted,

    /// <summary>An older sidecar still recorded a hash; it was dropped.</summary>
    Refreshed,

    /// <summary>A sidecar followed its asset to a new path, identity intact.</summary>
    Carried,

    /// <summary>A deleted asset's identity was put aside in case the delete was half of a move.</summary>
    Quarantined,

    /// <summary>A quarantined identity was reattached to an asset that reappeared elsewhere.</summary>
    Relinked,
}

/// <summary>
/// Keeps <c>*.meta</c> in step with the assets beside them: mints what is missing, carries one
/// that moved, and never destroys an identity.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProjectVerifier"/> already names every way sidecars rot — an orphan is "a move that
/// skipped the tooling", a missing one says "tooling owns sidecars; see the mv/import verbs". This
/// is that tooling. It holds all the RULES and none of the timing, so every one of them is
/// testable over a <c>MemoryFileSystem</c> with no clock; <see cref="AssetWatcher"/> owns the
/// events, the debounce and the quarantine window.
/// </para>
/// <para>
/// <b>Nothing here deletes an identity.</b> A deleted <c>.meta</c> is a GUID gone for good and every
/// reference to it broken, and a move performed by anything that emits no rename — <c>git mv</c> on
/// Windows, most of them — arrives as a delete followed by an add. So a delete QUARANTINES: the
/// file stays where it is, and its identity is held. An asset that reappears with matching content
/// takes that identity back. The one time a sidecar file is removed is the re-link inside <see cref="Ensure"/>, which
/// has already written the same identity at the asset's new path — a move, not a destruction.
/// </para>
/// <para>
/// Re-linking matches on a content hash held in memory for this session, not on a field in the
/// sidecar. A recorded hash is a checkout: text assets change bytes across machines (line
/// endings, smudge filters) after a push/pull, and writing that into a committed <c>.meta</c>
/// makes every clone a dirty tree. <see cref="Ensure"/> remembers what it saw; a delete that was
/// half of a move still re-links for the quarantine window. A sidecar that still has a leftover
/// <c>hash</c> is rewritten without it.
/// </para>
/// </remarks>
public sealed class SidecarMaintainer
{
    private readonly IFileSystem _fileSystem;
    private readonly AssetProjectLayout _layout;
    private readonly Action<string> _log;
    private readonly bool _dryRun;

    /// <summary>Identities of deleted assets, keyed by the content hash remembered this session.</summary>
    private readonly Dictionary<string, QuarantinedIdentity> _quarantine = [];

    /// <summary>
    /// Last content hash <see cref="Ensure"/> saw for a path. Quarantine uses this instead of a
    /// field in the sidecar, so a delete-then-add still re-links for the watch session.
    /// </summary>
    private readonly Dictionary<UPath, string> _seenHash = [];

    /// <summary>Creates a maintainer over one project.</summary>
    /// <param name="dryRun">Report what would happen and write nothing.</param>
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

    /// <summary>Identities held from deleted assets, awaiting an asset that matches them.</summary>
    public IReadOnlyCollection<string> Quarantined => _quarantine.Keys;

    /// <summary>Brings every asset under <c>assets/</c> into line. Returns how many it touched.</summary>
    /// <remarks>
    /// The one-shot form of the watcher, and what <c>--dry-run</c> reports from: a project whose
    /// sidecars drifted before anyone was watching is the normal starting state.
    /// </remarks>
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

    /// <summary>Gives an asset a sidecar, or brings the one it has up to date.</summary>
    /// <remarks>
    /// The add and change cases are one method because they are one question — "does the sidecar
    /// beside this asset describe it?" — and an editor's temp-then-rename save arrives as either,
    /// unpredictably.
    /// </remarks>
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
                // A malformed sidecar is the author's to fix: it may hold the only copy of an
                // identity, and overwriting it to make the warning go away would spend that
                // identity to tidy up a message.
                _log($"{Display(sidecar)}: left alone — {error.Message}");
                return SidecarAction.None;
            }

            if (existing.Hash is null) return SidecarAction.None;

            existing.Hash = null;
            Save(existing, sidecar);
            _log($"refreshed: {Display(sidecar)} (dropped recorded hash)");
            return SidecarAction.Refreshed;
        }

        // An asset appearing with content a deleted one had is that asset, moved by something that
        // reported no rename. Take the identity back rather than minting a stranger.
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

    /// <summary>Carries a sidecar to follow the asset it describes.</summary>
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

    /// <summary>Holds a deleted asset's identity, in case the delete was half of a move.</summary>
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

        // Prefer what this session saw; fall back to a leftover recorded hash on an old sidecar.
        // Neither is written back. No hash at all means we cannot re-link — the sidecar stays put
        // and the orphan is `verify`'s to report.
        if (!_seenHash.Remove(asset, out var hash)) hash = meta.Hash;
        if (hash is null) return SidecarAction.None;

        _quarantine[hash] = new QuarantinedIdentity(asset, sidecar, meta, at);
        _log($"quarantined: {Display(sidecar)} (identity held in case this is a move)");
        return SidecarAction.Quarantined;
    }

    /// <summary>Forgets identities held longer than a move plausibly takes.</summary>
    /// <remarks>
    /// Forgetting only drops the chance to RE-LINK. The sidecar itself is still on disk and the
    /// GUID still in it, so a delete that was really a delete leaves an orphan for <c>verify</c> to
    /// report — which it already does, in those words.
    /// </remarks>
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

/// <summary>An identity held from a deleted asset.</summary>
/// <param name="Asset">Where the asset was.</param>
/// <param name="Sidecar">Its sidecar, still on disk — quarantine holds, it does not remove.</param>
/// <param name="Meta">The identity itself.</param>
/// <param name="At">When it was quarantined — the caller's clock, so this type needs none.</param>
public readonly record struct QuarantinedIdentity(UPath Asset, UPath Sidecar, SidecarMeta Meta, DateTimeOffset At);
