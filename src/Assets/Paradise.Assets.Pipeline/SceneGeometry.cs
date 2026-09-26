using System.Numerics;

using Paradise.Assets.Documents;
using Paradise.Assets.Gltf;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// A level document as placed geometry: prefab instances expanded, every object carrying its
/// composed world transform, and a <c>.mesh</c> document decoded to raw triangles. Authored
/// bakes — navigation, colliders — share this walk; which objects PARTICIPATE is the caller's
/// rule, expressed over the component schemas.
/// </summary>
/// <remarks>
/// World composition duplicates what the game's loader does at runtime (row-vector
/// <c>local * parentWorld</c>, an unplaced object having no world at all) because the same
/// answer must fall out of the same document either way.
/// </remarks>
public static class SceneGeometry
{
    /// <summary>One resolved object and its world placement — null when the document gives it
    /// none (no local transform and no placed parent; a camera is such an object).</summary>
    public sealed record Entry(PrefabObject Object, Matrix4x4? World);

    /// <summary>A document with every prefab instance expanded and every world composed.</summary>
    public sealed class Scene
    {
        internal Scene(IReadOnlyList<Entry> objects, IReadOnlyDictionary<Guid, Entry> byGuid)
        {
            Objects = objects;
            ByGuid = byGuid;
        }

        public IReadOnlyList<Entry> Objects { get; }

        /// <summary>By <c>meta.Guid</c>; the parent chain is walked through it.</summary>
        public IReadOnlyDictionary<Guid, Entry> ByGuid { get; }

        /// <summary>The entry's parent, when its <c>meta.Parent</c> resolves.</summary>
        public Entry? ParentOf(Entry entry) =>
            entry.Object.Parent is { } id && ByGuid.TryGetValue(id, out var parent) ? parent : null;
    }

    /// <summary>
    /// Load <paramref name="document"/>, expand its prefab instances through
    /// <paramref name="index"/> and compose world transforms up the parent chain. Expansion
    /// problems are appended to <paramref name="errors"/>; publishing callers must refuse the
    /// incomplete result when that collection is nonempty.
    /// </summary>
    public static Scene Load(IFileSystem fileSystem, AssetIndex index, UPath document, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(errors);

        var parsed = PrefabDocumentSerializer.Load(fileSystem, document);
        return Resolve(parsed, reference => LoadPrefab(fileSystem, index, reference, errors), errors);
    }

    /// <summary>The resolve-and-compose half of <see cref="Load"/> over a document already read.</summary>
    public static Scene Resolve(PrefabDocument document, Func<AssetReference, PrefabDocument?> prefabs, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(prefabs);
        ArgumentNullException.ThrowIfNull(errors);

        var resolved = PrefabResolver.Resolve(document, prefabs);
        foreach (var error in resolved.Errors) errors.Add(error.Message);

        var objects = resolved.Document.Objects;
        var entries = new Entry?[objects.Count];
        var byGuid = new Dictionary<Guid, Entry>();
        var lookup = new Dictionary<Guid, int>();
        for (var i = 0; i < objects.Count; i++)
        {
            if (objects[i].Guid is { } guid) lookup.TryAdd(guid, i);
        }

        // Memoized and depth-first up the parent chain: a hierarchy N deep composes once, not
        // once per descendant. The serializer refuses parent cycles, but Resolve also takes
        // documents built in code — a cycle there is reported once and everything on it left
        // unplaced, rather than composing a position nobody authored.
        var worlds = new Matrix4x4?[objects.Count];
        var settled = new bool[objects.Count];
        var walking = new bool[objects.Count];
        var cyclic = new HashSet<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            var entry = new Entry(objects[i], WorldOf(i));
            entries[i] = entry;
            if (objects[i].Guid is { } guid) byGuid.TryAdd(guid, entry);
        }

        Matrix4x4? WorldOf(int index)
        {
            if (settled[index]) return worlds[index];
            if (walking[index])
            {
                // Re-entered while still composing: this object is on the cycle.
                cyclic.Add(index);
                errors.Add($"object '{objects[index].Name ?? DocumentGuid.Format(objects[index].Guid ?? Guid.Empty)}' sits in a parent cycle");
                return null;
            }

            walking[index] = true;

            var local = objects[index].Component(WellKnownComponents.TransformId) is { } transform
                ? Local(transform)
                : (Matrix4x4?)null;
            var parentIndex = objects[index].Parent is { } id && lookup.TryGetValue(id, out var found)
                ? found
                : -1;
            var parent = parentIndex >= 0 ? WorldOf(parentIndex) : null;

            walking[index] = false;
            settled[index] = true;

            // Everything that walks INTO a cycle hangs off it: unplaced, like its parent. This
            // also covers the nodes composing above the re-entry, so the whole loop is unplaced.
            if (parentIndex >= 0 && cyclic.Contains(parentIndex))
            {
                cyclic.Add(index);
                return null;
            }

            worlds[index] = (local, parent) switch
            {
                (null, null) => null,
                (null, { } above) => above,
                ({ } here, null) => here,
                ({ } here, { } above) => here * above,
            };
            return worlds[index];
        }

        static Matrix4x4 Local(PrefabComponent transform)
        {
            var trs = LocalTransformCodec.Read(transform.Data);
            return Matrix4x4.CreateScale(trs.Scale)
                * Matrix4x4.CreateFromQuaternion(trs.Rotation)
                * Matrix4x4.CreateTranslation(trs.Position);
        }

        return new Scene(Array.ConvertAll(entries, entry => entry!), byGuid);
    }

    /// <summary>
    /// Every leaf field of <paramref name="schema"/> declared as authored by host kind
    /// <paramref name="kind"/>, with the value the payload holds for it (null when unset —
    /// the field's <see cref="AuthoredFieldSchema.Default"/> is the caller's to apply). Composed
    /// objects and arrays are walked through, matching how an editor's plan reads the payload.
    /// </summary>
    public static IEnumerable<(AuthoredFieldSchema Field, object? Value)> HostValues(
        CanonicalTomlTable data, AuthoredComponentSchema? schema, string kind)
    {
        if (schema is null) yield break;
        foreach (var field in schema.Fields)
        {
            foreach (var found in Walk(data.Value(field.Name), field))
            {
                yield return found;
            }
        }

        IEnumerable<(AuthoredFieldSchema, object?)> Walk(object? value, AuthoredFieldSchema field)
        {
            if (field.AuthoredBy == kind) yield return (field, value);
            // Composed objects arrive as either table kind — an inline { ... } member or a
            // [[dotted]] sub-table — and arrays carry either inline elements or table elements.
            if (field.Fields is { } members && value is null or CanonicalInlineTable or CanonicalTomlTable)
            {
                foreach (var member in members)
                {
                    var memberValue = value is CanonicalInlineTable inline
                        ? inline.Value(member.Name)
                        : (value as CanonicalTomlTable)?.Value(member.Name);
                    foreach (var found in Walk(memberValue, member)) yield return found;
                }
            }
            else if (value is IReadOnlyList<object> items && field.Items is { } element)
            {
                foreach (var item in items)
                {
                    foreach (var found in Walk(item, element)) yield return found;
                }
            }
        }
    }

    /// <summary>
    /// Append the triangles a mesh-reference document stands for — its GLB's mesh instances with
    /// their node transforms baked in — transformed by <paramref name="world"/>. A mirrored
    /// (negative-determinant) world flips winding so faces keep pointing out. Returns false and
    /// reports when the reference does not resolve to readable geometry.
    /// </summary>
    public static bool AppendMeshTriangles(
        IFileSystem fileSystem,
        AssetIndex index,
        AssetReference meshDocument,
        Matrix4x4 world,
        List<float> vertices,
        List<int> indices,
        List<string> errors)
        => AppendMeshTriangles(fileSystem, index, meshDocument, world, vertices, indices, errors, null);

    internal static bool AppendMeshTriangles(
        IFileSystem fileSystem,
        AssetIndex index,
        AssetReference meshDocument,
        Matrix4x4 world,
        List<float> vertices,
        List<int> indices,
        List<string> errors,
        Dictionary<Guid, GltfAsset>? geometryCache)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(errors);

        var reference = index.Resolve(meshDocument);
        if (!reference.Found)
        {
            errors.Add($"mesh reference '{meshDocument.Path}' (guid {DocumentGuid.Format(meshDocument.Guid)}) does not resolve under assets/");
            return false;
        }

        MeshReferenceDocument document;
        try
        {
            document = MeshReferenceDocument.Load(fileSystem, reference.Asset);
        }
        catch (Exception failure) when (failure is FormatException or IOException)
        {
            errors.Add($"{reference.Path}: {failure.Message}");
            return false;
        }

        if (!MeshReferenceDocument.IsGeometry(document.Slot))
        {
            errors.Add($"{reference.Path}: is a '{MeshReferenceDocument.Spell(document.Slot)}' document, which carries no triangles");
            return false;
        }

        var glb = index.Resolve(document.Source);
        if (!glb.Found)
        {
            errors.Add($"{reference.Path}: names model '{document.Source.Path}', which no asset under assets/ carries");
            return false;
        }

        GltfAsset asset;
        try
        {
            if (geometryCache is null || !geometryCache.TryGetValue(document.Source.Guid, out asset!))
            {
                asset = GltfSceneReader.ReadGeometry(ModelSource.ReadGlb(fileSystem, glb.Asset));
                geometryCache?.Add(document.Source.Guid, asset);
            }
        }
        catch (Exception failure) when (failure is InvalidDataException or NotSupportedException or IOException)
        {
            errors.Add($"{reference.Path}: {glb.Path}: {failure.Message}");
            return false;
        }

        var appended = false;
        foreach (var instance in asset.Instances)
        {
            if (instance.MeshIndex < 0 || instance.MeshIndex >= asset.Meshes.Length)
            {
                errors.Add($"{reference.Path}: '{glb.Path}' holds an invalid mesh instance");
                return false;
            }
            var transform = instance.WorldTransform * world;
            var mirrored = transform.GetDeterminant() < 0f;
            foreach (var primitive in asset.Meshes[instance.MeshIndex].Primitives)
            {
                if (primitive.Indices.Length % 3 != 0)
                {
                    errors.Add($"{reference.Path}: '{glb.Path}' contains a mesh primitive with incomplete triangles");
                    return false;
                }
                var origin = vertices.Count / 3;
                for (var vertex = 0; vertex < primitive.VertexCount; vertex++)
                {
                    var at = vertex * GltfPrimitive.FloatsPerVertex;
                    var point = Vector3.Transform(
                        new Vector3(primitive.Vertices[at], primitive.Vertices[at + 1], primitive.Vertices[at + 2]),
                        transform);
                    vertices.Add(point.X);
                    vertices.Add(point.Y);
                    vertices.Add(point.Z);
                }

                foreach (var corner in primitive.Indices)
                {
                    indices.Add(origin + (int)corner);
                }

                if (mirrored)
                {
                    for (var i = indices.Count - primitive.Indices.Length; i < indices.Count; i += 3)
                    {
                        (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
                    }
                }

                appended = true;
            }
        }

        if (!appended)
        {
            errors.Add($"{reference.Path}: '{glb.Path}' holds no mesh instances");
            return false;
        }

        return true;
    }

    private static PrefabDocument? LoadPrefab(IFileSystem fileSystem, AssetIndex index, AssetReference reference, List<string> errors)
    {
        var resolution = index.Resolve(reference);
        if (!resolution.Found)
        {
            errors.Add($"prefab '{reference.Path}' (guid {DocumentGuid.Format(reference.Guid)}) does not resolve under assets/");
            return null;
        }

        try
        {
            return PrefabDocumentSerializer.Load(fileSystem, resolution.Asset);
        }
        catch (PrefabDocumentException failure)
        {
            errors.Add(failure.Message);
            return null;
        }
    }
}
