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
/// <param name="Asset">The absolute path of the asset the guid names; the path half's target when nothing carries the guid.</param>
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
/// Every asset under <c>assets/</c> by its GUID, taken once per run from the sidecars.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes a reference's path a HINT. A rename done in Finder or with <c>git mv</c>
/// carries the sidecar along (or <c>watch</c> relinks the identity by content hash), so the guid
/// still names the asset while every document that references it still spells the old path. That
/// must not break a build, and before this index it did: every consumer resolved by path.
/// </para>
/// <para>
/// A duplicate guid keeps the FIRST asset in scan order, which is ordinal and therefore stable;
/// <c>verify</c> reports the duplicate against the second sidecar, and resolving to either of two
/// assets that claim one identity would be arbitrary anyway.
/// </para>
/// </remarks>
public sealed class AssetIndex
{
    private readonly AssetPaths _sources;
    private readonly Dictionary<Guid, UPath> _byGuid;
    private readonly Dictionary<UPath, Guid> _byPath;
    private readonly HashSet<UPath> _withoutIdentity;

    private AssetIndex(AssetPaths sources, Dictionary<Guid, UPath> byGuid, Dictionary<UPath, Guid> byPath, HashSet<UPath> withoutIdentity)
    {
        _sources = sources;
        _byGuid = byGuid;
        _byPath = byPath;
        _withoutIdentity = withoutIdentity;
    }

    /// <summary>An index over a tree that has none: every reference is <see cref="ReferenceStatus.Unresolved"/>.</summary>
    public static AssetIndex Empty(AssetPaths sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return new AssetIndex(sources, [], [], []);
    }

    /// <summary>Reads every sidecar in <paramref name="sources"/>; an unreadable one is not a failure here, because <c>verify</c> reports it against the sidecar itself.</summary>
    public static AssetIndex Build(IFileSystem fileSystem, AssetPaths sources, AssetIgnoreRules? ignore = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(sources);

        var rules = ignore ?? AssetIgnoreRules.None;
        var byGuid = new Dictionary<Guid, UPath>();
        var byPath = new Dictionary<UPath, Guid>();
        var withoutIdentity = new HashSet<UPath>();

        foreach (var path in sources.Files)
        {
            if (SidecarMeta.IsSidecarPath(path) || rules.Matches(sources.Root, path)) continue;

            var sidecar = SidecarMeta.PathFor(path);
            if (!sources.Contains(sidecar))
            {
                withoutIdentity.Add(path);
                continue;
            }

            try
            {
                var guid = SidecarMeta.Load(fileSystem, sidecar).Guid;
                byGuid.TryAdd(guid, path);
                byPath[path] = guid;
            }
            catch (SidecarMetaException)
            {
                withoutIdentity.Add(path);
            }
        }

        return new AssetIndex(sources, byGuid, byPath, withoutIdentity);
    }

    /// <summary>The tree this index was taken over.</summary>
    public AssetPaths Sources => _sources;

    /// <summary>The asset carrying <paramref name="guid"/>, or null.</summary>
    public UPath? Find(Guid guid) => _byGuid.TryGetValue(guid, out var path) ? path : (UPath?)null;

    /// <summary>Resolves a reference: the guid decides, the path is only a hint.</summary>
    public ReferenceResolution Resolve(AssetReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var hinted = Hinted(reference.Path);
        var hintIdentity = IdentityAt(hinted);

        if (_byGuid.TryGetValue(reference.Guid, out var asset))
        {
            return asset == hinted
                ? new ReferenceResolution(reference, ReferenceStatus.Resolved, asset, reference.Path, hintIdentity)
                : new ReferenceResolution(reference, ReferenceStatus.Stale, asset, _sources.Relative(asset), hintIdentity);
        }

        var status = hinted is { } target && _withoutIdentity.Contains(target)
            ? ReferenceStatus.Undetermined
            : ReferenceStatus.Unresolved;

        return new ReferenceResolution(reference, status, hinted ?? default, reference.Path, hintIdentity);
    }

    /// <summary>The absolute path of the asset a reference names — by guid, falling back to its path half so an unresolvable reference still points somewhere nameable.</summary>
    public UPath AssetOf(AssetReference reference) => Resolve(reference).Asset;

    /// <summary>The assets-relative path a reference should spell.</summary>
    public string PathOf(AssetReference reference) => Resolve(reference).Path;

    private Guid? IdentityAt(UPath? hinted)
        => hinted is { } target && _byPath.TryGetValue(target, out var guid) ? guid : null;

    /// <summary>Null when the path half cannot even be combined onto the root (an absolute path, or one that climbs out of the mount).</summary>
    private UPath? Hinted(string path)
    {
        try
        {
            return (_sources.Root / path).ToAbsolute();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
