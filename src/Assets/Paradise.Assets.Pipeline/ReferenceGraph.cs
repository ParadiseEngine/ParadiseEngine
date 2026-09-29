using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>One authored reference, as an edge between identities.</summary>
/// <param name="Referrer">The identity of the file holding the reference (its own sidecar's guid).</param>
/// <param name="ReferrerPath">Where that file was when the graph was taken.</param>
/// <param name="Target">The identity referenced. Nothing may carry it any more: the edge is kept so the dangling reference can still be named.</param>
/// <param name="Where">The field the reference sits at, as <c>verify</c> reports it (<c>game.Mesh.Slots[0]</c>, <c>prefab</c>, <c>images[2]</c>).</param>
/// <param name="Path">The path half as written — a hint, possibly stale.</param>
public readonly record struct ReferenceEdge(Guid Referrer, UPath ReferrerPath, Guid Target, string Where, string Path);

/// <summary>
/// Who references what, by identity, over the whole tree: the answer to "what breaks if this
/// moves or goes", which every consumer used to compute by walking every document itself.
/// </summary>
/// <remarks>
/// <para>
/// DERIVED from <see cref="AssetIndex"/> plus the actual documents, and never stored on disk.
/// Startup reads the tree once; a single owner then replaces each changed file's outgoing
/// edges. Nodes are guids because paths are hints (#243), so a reference into a deleted asset
/// keeps pointing at the identity that is gone — exactly when someone asks who pointed there.
/// </para>
/// <para>
/// Referrers are whatever the importer chain claims (<see cref="IAssetImporter.References"/>); a file without an
/// identity of its own can reference but cannot be referenced, and is listed in
/// <see cref="Unreadable"/> along with anything that would not parse, so a verb that acts on the
/// graph can say "and N files could not be checked" rather than silently miss them.
/// </para>
/// </remarks>
public sealed class ReferenceGraph
{
    private readonly List<ReferenceEdge> _edges = [];
    private readonly Dictionary<Guid, List<ReferenceEdge>> _byTarget = [];
    private readonly Dictionary<Guid, List<ReferenceEdge>> _byReferrer = [];
    private readonly Dictionary<UPath, List<ReferenceEdge>> _byPath = [];
    private readonly List<UPath> _unreadable = [];
    private readonly List<(UPath Asset, ReferenceSite Site)> _pathOnly = [];

    private ReferenceGraph()
    {
    }

    /// <summary>Every edge, in the ordinal order of the files they were read from.</summary>
    public IReadOnlyList<ReferenceEdge> Edges => _edges;

    /// <summary>Files whose references could not be taken: no identity of their own, or a document that would not parse. A consumer acting on the graph walks these itself or says it could not check them.</summary>
    public IReadOnlyList<UPath> Unreadable => _unreadable;

    /// <summary>Sites that carry only a path (a container uri nothing has recorded yet): not edges, since they name nothing by guid, and the one kind of reference a move can only warn about.</summary>
    public IReadOnlyList<(UPath Asset, ReferenceSite Site)> PathOnly => _pathOnly;

    /// <summary>Asks the importer chain about every file under <paramref name="index"/>; the built-ins when <paramref name="importers"/> is omitted.</summary>
    public static ReferenceGraph Build(
        IFileSystem fileSystem, AssetProjectLayout layout, AssetIndex index, AssetIgnoreRules? ignore = null, IReadOnlyList<IAssetImporter>? importers = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(index);

        var context = new ReferenceContext(fileSystem, layout, index, ignore ?? AssetIgnoreRules.None);
        var chain = importers ?? AssetImporters.All;
        var graph = new ReferenceGraph();
        foreach (var path in index.Files)
        {
            if (!SidecarMeta.IsSidecarPath(path)) graph.Refresh(context, chain, path);
        }

        return graph;
    }
    /// <summary>Replaces only one file's outgoing references and unreadable or path-only state.</summary>
    /// <remarks>Refresh the index first; either an asset or its sidecar may be supplied. Remove the old referrer path before refreshing a rename's destination. Incoming references are never discarded when a target disappears or changes identity.</remarks>
    public void Refresh(
        IFileSystem fs, AssetProjectLayout layout, AssetIndex index, UPath path,
        AssetIgnoreRules? ignore = null, IReadOnlyList<IAssetImporter>? importers = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(index);
        path.AssertAbsolute(nameof(path));
        if (!index.IsUnderRoot(path)) return;
        if (SidecarMeta.IsSidecarPath(path)) path = SidecarMeta.AssetPathFor(path);
        Refresh(new ReferenceContext(fs, layout, index, ignore ?? AssetIgnoreRules.None), importers ?? AssetImporters.All, path);
    }

    /// <summary>Forgets one referrer's outgoing edges, never other files' incoming edges to its identity.</summary>
    /// <remarks>A sidecar path removes its owner's graph state; refresh the owner after its index identity changes.</remarks>
    public void Remove(UPath path)
    {
        path.AssertAbsolute(nameof(path));
        if (SidecarMeta.IsSidecarPath(path)) path = SidecarMeta.AssetPathFor(path);
        if (_byPath.Remove(path, out var edges))
        {
            RemoveFileEntries(_edges, path, static edge => edge.ReferrerPath);
            foreach (var edge in edges)
            {
                RemoveEdge(_byTarget, edge.Target, edge);
                RemoveEdge(_byReferrer, edge.Referrer, edge);
            }
        }

        RemoveFileEntries(_unreadable, path, static asset => asset);
        RemoveFileEntries(_pathOnly, path, static entry => entry.Asset);
    }

    private void Refresh(ReferenceContext context, IReadOnlyList<IAssetImporter> chain, UPath path)
    {
        Remove(path);
        if (!context.Index.Contains(path) || context.Index.IsIgnored(path) || context.Ignore.Matches(context.Index.Root, path)) return;
        try
        {
            if (ReferenceChain.Claim(chain, context, path) is not { } claimed) return;
            if (claimed.References.Problem is not null || context.Index.IdentityOf(path) is not { } referrer)
            {
                Insert(_unreadable, path, path, static asset => asset);
                return;
            }

            foreach (var site in claimed.References.Sites)
            {
                if (site.Reference is { } reference)
                {
                    Add(new ReferenceEdge(referrer, path, reference.Guid, site.Where, reference.Path));
                }
                else
                {
                    Insert(_pathOnly, (Asset: path, Site: site), path, static entry => entry.Asset);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or SidecarMetaException)
        {
            // A document can disappear or become unreadable between the event and its read.
            // Do not retain stale edges or let a partially-read file look successfully checked.
            Remove(path);
            Insert(_unreadable, path, path, static asset => asset);
        }
    }

    /// <summary>Every reference INTO <paramref name="asset"/>: what a move must follow and a delete would break.</summary>
    public IReadOnlyList<ReferenceEdge> DependentsOf(Guid asset)
        => _byTarget.TryGetValue(asset, out var edges) ? edges : [];

    /// <summary>Every reference OUT OF the file carrying <paramref name="referrer"/>.</summary>
    public IReadOnlyList<ReferenceEdge> DependenciesOf(Guid referrer)
        => _byReferrer.TryGetValue(referrer, out var edges) ? edges : [];

    /// <summary>The referrers of <paramref name="asset"/>, and theirs, and so on — a level counts as depending on the texture its prefab's mesh samples.</summary>
    public IReadOnlySet<Guid> TransitiveDependentsOf(Guid asset)
    {
        var seen = new HashSet<Guid>();
        var frontier = new Stack<Guid>();
        frontier.Push(asset);
        while (frontier.TryPop(out var current))
        {
            foreach (var edge in DependentsOf(current))
            {
                if (seen.Add(edge.Referrer)) frontier.Push(edge.Referrer);
            }
        }

        return seen;
    }

    /// <summary>The files that reference <paramref name="asset"/>, each once, in the order the graph first saw them.</summary>
    public IReadOnlyList<UPath> DependentFilesOf(Guid asset)
        => DependentsOf(asset).Select(edge => edge.ReferrerPath).Distinct().ToList();

    private void Add(ReferenceEdge edge)
    {
        Insert(_edges, edge, edge.ReferrerPath, static entry => entry.ReferrerPath);
        Insert(Bucket(_byTarget, edge.Target), edge, edge.ReferrerPath, static entry => entry.ReferrerPath);
        Insert(Bucket(_byReferrer, edge.Referrer), edge, edge.ReferrerPath, static entry => entry.ReferrerPath);
        if (!_byPath.TryGetValue(edge.ReferrerPath, out var edges))
        {
            edges = [];
            _byPath.Add(edge.ReferrerPath, edges);
        }

        edges.Add(edge);
    }

    private static List<ReferenceEdge> Bucket(Dictionary<Guid, List<ReferenceEdge>> map, Guid key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        return list;
    }

    private static void RemoveEdge(Dictionary<Guid, List<ReferenceEdge>> map, Guid key, ReferenceEdge edge)
    {
        var edges = map[key];
        edges.Remove(edge);
        if (edges.Count == 0) map.Remove(key);
    }

    private static void Insert<T>(List<T> entries, T entry, UPath path, Func<T, UPath> pathOf)
        => entries.Insert(Bound(entries, path, pathOf, after: true), entry);

    private static void RemoveFileEntries<T>(List<T> entries, UPath path, Func<T, UPath> pathOf)
    {
        var start = Bound(entries, path, pathOf, after: false);
        var end = start;
        while (end < entries.Count && pathOf(entries[end]) == path) end++;
        if (end > start) entries.RemoveRange(start, end - start);
    }

    private static int Bound<T>(List<T> entries, UPath path, Func<T, UPath> pathOf, bool after)
    {
        var low = 0;
        var high = entries.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            var comparison = StringComparer.Ordinal.Compare(pathOf(entries[middle]).FullName, path.FullName);
            if (comparison < 0 || (after && comparison == 0)) low = middle + 1;
            else high = middle;
        }

        return low;
    }
}
