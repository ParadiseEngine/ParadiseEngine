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

    /// <summary>An asset's sidecar hash caught up with its bytes.</summary>
    Refreshed,

    /// <summary>A sidecar followed its asset to a new path, identity intact.</summary>
    Carried,

    /// <summary>A deleted asset's identity was put aside in case the delete was half of a move.</summary>
    Quarantined,

    /// <summary>A quarantined identity was reattached to an asset that reappeared elsewhere.</summary>
    Relinked,
}

/// <summary>
/// Keeps <c>*.meta</c> in step with the assets beside them: mints what is missing, refreshes what
/// has changed, carries one that moved, and never destroys an identity.
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
/// Re-linking matches on the sidecar's recorded <c>hash</c>, which is exactly the job that field
/// was given: "what lets a LOST sidecar be re-linked, because content is the only thing left to
/// recognise an asset by once its id is gone". It follows that a STALE hash costs a move its
/// identity — and equally that a maintainer left running keeps hashes fresh, which is what makes
/// the next move safe. The two halves are the same loop.
/// </para>
/// </remarks>
public sealed class SidecarMaintainer
{
    private readonly IFileSystem _fileSystem;
    private readonly AssetProjectLayout _layout;
    private readonly Action<string> _log;
    private readonly bool _dryRun;

    /// <summary>Identities of deleted assets, by the content hash their sidecar recorded.</summary>
    private readonly Dictionary<string, QuarantinedIdentity> _quarantine = [];

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
        var kind = AssetClassifier.RequiredKind(AssetClassifier.Classify(_layout.Assets, asset), asset);
        if (kind is null || !_fileSystem.FileExists(asset)) return SidecarAction.None;

        var sidecar = SidecarMeta.PathFor(asset);
        var hash = SidecarMeta.ComputeHash(_fileSystem, asset);

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

            if (existing.Hash == hash) return SidecarAction.None;

            existing.Hash = hash;
            Save(existing, sidecar);
            _log($"refreshed: {Display(sidecar)}");
            return SidecarAction.Refreshed;
        }

        // An asset appearing with content a deleted one had is that asset, moved by something that
        // reported no rename. Take the identity back rather than minting a stranger.
        if (_quarantine.Remove(hash, out var held))
        {
            var restored = new SidecarMeta(held.Meta.Guid, held.Meta.Kind)
            {
                Hash = hash,
                Texture = held.Meta.Texture,
            };
            Save(restored, sidecar);
            Remove(held.Sidecar);
            _log($"relinked: {Display(held.Asset)} -> {Display(asset)} (guid kept)");
            return SidecarAction.Relinked;
        }

        var minted = new SidecarMeta(Guid.NewGuid(), kind.Value) { Hash = hash };
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

        // The hash goes with it unexamined. A rename does not change content, and re-reading the
        // bytes of a file that may still be settling is how a move ends up recording a half-written
        // hash -- Ensure will correct it on the change event if anything really did move.
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

        // No recorded hash, nothing to recognise it by later. The sidecar still stays put -- the
        // orphan is `verify`'s to report, and an identity is not ours to spend.
        if (meta.Hash is not { } hash) return SidecarAction.None;

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
