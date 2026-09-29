using System.Runtime.InteropServices;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>How an <see cref="AssetReference"/> resolved against the tree.</summary>
public enum ReferenceStatus
{
    /// <summary>The guid names an asset, and the path half spells where it is.</summary>
    Resolved,

    /// <summary>The guid names an asset that has since moved; the path half is out of date and the reference still resolves.</summary>
    Stale,

    /// <summary>No asset under <c>assets/</c> carries the guid. The reference names nothing.</summary>
    Unresolved,

    /// <summary>
    /// No asset carries the guid, but the path names one whose identity could not be read (no
    /// sidecar, or an unreadable one). Reported against that sidecar, not against every reference
    /// into it.
    /// </summary>
    Undetermined,
}

/// <summary>What one <see cref="AssetReference"/> resolved to.</summary>
/// <param name="Reference">The reference as authored.</param>
/// <param name="Status">Which of the four cases this is.</param>
/// <param name="Asset">The absolute path of the asset the guid names; the path half's target when nothing carries the guid. NULL (<c>IsNull</c>) when nothing carries the guid and the path half cannot even be combined onto the root (one that climbs above it; an absolute path merely resolves to itself and fails <see cref="AssetIndex.Problem"/>), so every consumer that opens it checks <see cref="Found"/> first.</param>
/// <param name="Path">The assets-relative, '/'-separated path the reference SHOULD spell; unchanged from the reference when it did not resolve.</param>
/// <param name="HintIdentity">The identity of whatever the path half names, when that asset exists and has one. Only interesting when the two disagree.</param>
public readonly record struct ReferenceResolution(
    AssetReference Reference,
    ReferenceStatus Status,
    UPath Asset,
    string Path,
    Guid? HintIdentity)
{
    /// <summary>Whether the reference named a real asset, by either half.</summary>
    public bool Found => Status is ReferenceStatus.Resolved or ReferenceStatus.Stale;

    /// <summary>The reference as it would be written now: same identity, current path.</summary>
    public AssetReference Current => Status == ReferenceStatus.Stale ? Reference with { Path = Path } : Reference;
}

/// <summary>
/// The files under <c>assets/</c> and their identities, initialized by one ordinal scan and
/// maintained by explicit file updates.
/// </summary>
/// <remarks>
/// <para>
/// <b>One tree, one object.</b> The file set and the guid map are maintained together, so every
/// consumer sees the same identities and membership. Updates belong to the caller's single
/// drainer; this object does not watch the filesystem or synchronize concurrent mutations.
/// </para>
/// <para>
/// <b>Paths are matched exactly, not by <c>FileExists</c>.</b> The OS below may be
/// case-insensitive (macOS, Windows) or normalisation-insensitive (APFS), so
/// <c>../Textures/Rust.png</c> passes there, the KTX2 is written at the real case, and the
/// shipped mesh points at a file Linux cannot find (issue #202). The set holds exactly the names
/// the directory walk returned, so a path resolves only when it is spelled as the file is.
/// </para>
/// <para>
/// <b>An <see cref="AssetReference"/> resolves by GUID; its path is a hint.</b> A rename done in
/// Finder or with <c>git mv</c> carries the sidecar along (or <c>watch</c> relinks the identity by
/// content hash), so the guid still names the asset while every document that references it still
/// spells the old path. That must not break a build, and before this index it did: every consumer
/// resolved by path. A duplicate guid keeps the FIRST asset in scan order, which is ordinal and
/// therefore stable; <c>verify</c> reports the duplicate against the second sidecar, and resolving
/// to either of two assets that claim one identity would be arbitrary anyway.
/// </para>
/// </remarks>
public sealed class AssetIndex
{
    private static readonly IComparer<UPath> s_pathOrder = Comparer<UPath>.Create(
        static (left, right) => StringComparer.Ordinal.Compare(left.FullName, right.FullName));
    private readonly HashSet<UPath> _files;
    private readonly List<UPath> _orderedFiles;
    private readonly Dictionary<string, SortedSet<UPath>> _byFoldedName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, SortedSet<UPath>> _byGuid = [];
    private readonly Dictionary<UPath, Guid> _byPath = [];
    private readonly HashSet<UPath> _withoutIdentity = [];
    private readonly HashSet<UPath> _ignored = [];
    private readonly AssetIgnoreRules _ignore;

    private AssetIndex(UPath root, List<UPath> files, AssetIgnoreRules ignore)
    {
        Root = root;
        _orderedFiles = files;
        _files = [.. files];
        _ignore = ignore;
        foreach (var file in files) AddFoldedName(file);
    }

    /// <summary>Walks <paramref name="assetsRoot"/> once and reads every sidecar in it.</summary>
    /// <param name="ignore">The project's <c>[assets] ignore</c>. An ignored file carries no identity into the index; verify reports a sidecar found beside one (issue #203).</param>
    /// <remarks>An unreadable sidecar is not a failure here: <c>verify</c> reports it against the sidecar itself, and the asset it describes is remembered as one whose identity could not be read.</remarks>
    public static AssetIndex Scan(IFileSystem fileSystem, UPath assetsRoot, AssetIgnoreRules? ignore = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        assetsRoot.AssertAbsolute(nameof(assetsRoot));

        var files = fileSystem.DirectoryExists(assetsRoot)
            ? fileSystem.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories)
                .OrderBy(p => p.FullName, StringComparer.Ordinal)
                .ToList()
            : [];

        var index = new AssetIndex(assetsRoot, files, ignore ?? AssetIgnoreRules.None);
        foreach (var path in files) index.ReadIdentity(fileSystem, path, index._ignore);
        return index;
    }

    public UPath Root { get; }

    /// <summary>Every file, sidecars and junk included, in ordinal order.</summary>
    public IReadOnlyList<UPath> Files => _orderedFiles;

    public bool IsUnderRoot(UPath path) => path.IsInDirectory(Root, recursive: true);

    /// <summary>Case- and normalisation-exact.</summary>
    public bool Contains(UPath path) => _files.Contains(path);

    /// <summary>The real spelling of a path that differs only by case, for an error message that says what to fix.</summary>
    public bool TryFindIgnoringCase(UPath path, out UPath actual)
    {
        actual = _byFoldedName.TryGetValue(path.FullName, out var paths) ? paths.Min : default;
        return !actual.IsNull && actual != path;
    }

    public string Relative(UPath path) => path.FullName[(Root.FullName.Length + 1)..];

    /// <summary>
    /// What went wrong with a PATH, or null when it names a file here; the message continues
    /// "<c>{source}: references '{reference}', which …</c>".
    /// </summary>
    /// <remarks>
    /// About the path alone. An authored reference carries a guid too, and that guid decides — see
    /// <see cref="Resolve"/>; this answers only for paths a file format carries with no identity
    /// beside them (a GLB's image uris) and for phrasing the case where an identity resolved to
    /// nothing either.
    /// </remarks>
    public string? Problem(UPath resolved, string reference)
    {
        if (!IsUnderRoot(resolved))
        {
            return $"references '{reference}', which resolves outside assets/ ('{resolved}'); a build cannot ship what it does not own";
        }

        if (Contains(resolved)) return null;

        if (TryFindIgnoringCase(resolved, out var actual))
        {
            return $"references '{reference}', which does not exist under assets/ — '{Relative(actual)}' does, and " +
                "references are case-exact because a build that passes on this machine ships a path Linux cannot open";
        }

        return $"references '{reference}', which does not exist under assets/";
    }

    /// <summary>The asset carrying <paramref name="guid"/>, or null.</summary>
    public UPath? Find(Guid guid) => _byGuid.TryGetValue(guid, out var paths) ? paths.Min : (UPath?)null;

    /// <summary>Every asset carrying <paramref name="guid"/>, in ordinal order, including duplicate claims.</summary>
    public IReadOnlyCollection<UPath> PathsOf(Guid guid) => _byGuid.TryGetValue(guid, out var paths) ? paths : [];

    /// <summary>Refreshes one asset and its sidecar without enumerating the tree.</summary>
    /// <remarks>The path may name either file and supplies its exact spelling. Remove old rename paths first, especially on case-insensitive filesystems. Omitted ignore rules retain the scan's rules; changed project-wide rules require a new scan.</remarks>
    public void Refresh(IFileSystem fileSystem, UPath path, AssetIgnoreRules? ignore = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        path.AssertAbsolute(nameof(path));
        if (!IsUnderRoot(path)) return;
        var asset = SidecarMeta.IsSidecarPath(path) ? SidecarMeta.AssetPathFor(path) : path;
        if (!IsUnderRoot(asset)) return;
        var sidecar = SidecarMeta.PathFor(asset);
        RefreshMembership(fileSystem, asset);
        RefreshMembership(fileSystem, sidecar);
        if (Contains(asset)) ReadIdentity(fileSystem, asset, ignore ?? _ignore);
        else
        {
            ForgetIdentity(asset);
            _ignored.Remove(asset);
        }
    }

    /// <summary>Forgets exactly one authoritative old or deleted path without consulting the filesystem.</summary>
    /// <remarks>Removing an asset leaves any indexed sidecar as an orphan; remove both old paths for a rename. Removing a sidecar immediately clears its owner's identity, preserving the owner as undetermined unless ignored.</remarks>
    public void Remove(UPath path)
    {
        path.AssertAbsolute(nameof(path));
        if (!IsUnderRoot(path)) return;
        RemoveMembership(path);
        var asset = SidecarMeta.IsSidecarPath(path) ? SidecarMeta.AssetPathFor(path) : path;
        ForgetIdentity(asset);
        if (asset == path) _ignored.Remove(asset);
        else if (Contains(asset) && !IsIgnored(asset)) _withoutIdentity.Add(asset);
    }

    /// <summary>Resolves a reference: the guid decides, the path is only a hint.</summary>
    public ReferenceResolution Resolve(AssetReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var hinted = Hinted(reference.Path);
        var hintIdentity = hinted is { } named && _byPath.TryGetValue(named, out var identity) ? identity : (Guid?)null;

        if (Find(reference.Guid) is { } asset)
        {
            return asset == hinted
                ? new ReferenceResolution(reference, ReferenceStatus.Resolved, asset, reference.Path, hintIdentity)
                : new ReferenceResolution(reference, ReferenceStatus.Stale, asset, Relative(asset), hintIdentity);
        }

        var status = hinted is { } target && _withoutIdentity.Contains(target)
            ? ReferenceStatus.Undetermined
            : ReferenceStatus.Unresolved;

        return new ReferenceResolution(reference, status, hinted ?? default, reference.Path, hintIdentity);
    }

    /// <summary>The absolute path of the asset a reference names — by guid, falling back to its path half so an unresolvable reference still points somewhere nameable. Null when even the path half names nothing combinable (see <see cref="ReferenceResolution.Asset"/>).</summary>
    public UPath AssetOf(AssetReference reference) => Resolve(reference).Asset;

    /// <summary>The identity the asset at <paramref name="path"/> carries, or null when it has none the index could read.</summary>
    public Guid? IdentityOf(UPath path) => _byPath.TryGetValue(path, out var guid) ? guid : null;

    /// <summary>Whether <paramref name="path"/> is a file the manifest's <c>[assets] ignore</c> excludes, and so carries no identity by design rather than by omission.</summary>
    public bool IsIgnored(UPath path) => _ignored.Contains(path);

    private void ReadIdentity(IFileSystem fileSystem, UPath path, AssetIgnoreRules ignore)
    {
        if (SidecarMeta.IsSidecarPath(path)) return;
        if (ignore.Matches(Root, path))
        {
            ForgetIdentity(path);
            _ignored.Add(path);
            return;
        }

        _ignored.Remove(path);
        var sidecar = SidecarMeta.PathFor(path);
        if (!Contains(sidecar))
        {
            ForgetIdentity(path);
            _withoutIdentity.Add(path);
            return;
        }

        try
        {
            var guid = SidecarMeta.Load(fileSystem, sidecar).Guid;
            if (_byPath.TryGetValue(path, out var previous) && previous == guid) return;
            ForgetIdentity(path);
            if (!_byGuid.TryGetValue(guid, out var paths))
            {
                paths = new SortedSet<UPath>(s_pathOrder);
                _byGuid.Add(guid, paths);
            }

            paths.Add(path);
            _byPath[path] = guid;
        }
        catch (SidecarMetaException)
        {
            ForgetIdentity(path);
            _withoutIdentity.Add(path);
        }
    }

    private void ForgetIdentity(UPath path)
    {
        _withoutIdentity.Remove(path);
        if (!_byPath.Remove(path, out var guid)) return;
        var paths = _byGuid[guid];
        paths.Remove(path);
        if (paths.Count == 0) _byGuid.Remove(guid);
    }

    private void RefreshMembership(IFileSystem fileSystem, UPath path)
    {
        if (!fileSystem.FileExists(path))
        {
            RemoveMembership(path);
            return;
        }

        if (!_files.Add(path)) return;
        // Coyote 1.7 rewrites List.BinarySearch to a void-returning wrapper; the span overload
        // keeps this single-owner lookup allocation-free and leaves queue instrumentation intact.
        _orderedFiles.Insert(~CollectionsMarshal.AsSpan(_orderedFiles).BinarySearch(path, s_pathOrder), path);
        AddFoldedName(path);
    }

    private void AddFoldedName(UPath path)
    {
        if (!_byFoldedName.TryGetValue(path.FullName, out var paths))
        {
            paths = new SortedSet<UPath>(s_pathOrder);
            _byFoldedName.Add(path.FullName, paths);
        }

        paths.Add(path);
    }

    private void RemoveMembership(UPath path)
    {
        if (!_files.Remove(path)) return;
        _orderedFiles.RemoveAt(CollectionsMarshal.AsSpan(_orderedFiles).BinarySearch(path, s_pathOrder));
        var paths = _byFoldedName[path.FullName];
        paths.Remove(path);
        if (paths.Count == 0) _byFoldedName.Remove(path.FullName);
    }

    /// <summary>Null when the path half cannot even be combined onto the root (it climbs above the mount). An absolute path combines to itself and is caught downstream as outside assets/.</summary>
    private UPath? Hinted(string path)
    {
        try
        {
            return (Root / path).ToAbsolute();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
