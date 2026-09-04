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
/// DERIVED from <see cref="AssetIndex"/> plus the files, and never stored: a reference list kept in
/// a sidecar is a second copy of the document that a watcher may or may not be running to keep in
/// step, and it dirties two files per edit. ShiningPie has thirty documents; the walk is not
/// what a build's time goes on. Nodes are guids because paths are hints (#243), so a renamed
/// level keeps its edges and a reference into a deleted asset keeps pointing at the identity that
/// is gone — which is exactly the moment someone asks who pointed there.
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
            if (SidecarMeta.IsSidecarPath(path)) continue;
            if (ReferenceChain.Claim(chain, context, path) is not { } claimed) continue;

            if (claimed.References.Problem is not null || index.IdentityOf(path) is not { } referrer)
            {
                graph._unreadable.Add(path);
                continue;
            }

            foreach (var site in claimed.References.Sites)
            {
                if (site.Reference is { } reference)
                {
                    graph.Add([new ReferenceEdge(referrer, path, reference.Guid, site.Where, reference.Path)]);
                }
                else
                {
                    graph._pathOnly.Add((path, site));
                }
            }
        }

        return graph;
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

    /// <summary>Swaps in a file's edges after it changed, so the watcher need not rebuild the graph per save.</summary>
    public void Replace(Guid referrer, IEnumerable<ReferenceEdge> fresh)
    {
        ArgumentNullException.ThrowIfNull(fresh);

        Forget(referrer);
        Add(fresh);
    }

    /// <summary>Drops every edge out of the file carrying <paramref name="referrer"/>. Edges INTO it stay: they are other files' references, and now dangling.</summary>
    public void Forget(Guid referrer)
    {
        if (!_byReferrer.Remove(referrer, out var edges)) return;

        _edges.RemoveAll(edge => edge.Referrer == referrer);
        foreach (var edge in edges)
        {
            if (_byTarget.TryGetValue(edge.Target, out var targets))
            {
                targets.RemoveAll(candidate => candidate.Referrer == referrer);
                if (targets.Count == 0) _byTarget.Remove(edge.Target);
            }
        }
    }

    private void Add(IEnumerable<ReferenceEdge> edges)
    {
        foreach (var edge in edges)
        {
            _edges.Add(edge);
            Bucket(_byTarget, edge.Target).Add(edge);
            Bucket(_byReferrer, edge.Referrer).Add(edge);
        }
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
}
