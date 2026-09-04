using System.Text.Json.Nodes;

using Paradise.Assets.Documents;
using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>One external image of a GLB: where the file says it is, and the identity it carries, if any.</summary>
/// <param name="ImageIndex">Index into the glTF <c>images</c> array.</param>
/// <param name="Uri">The uri as written, relative to the GLB's own directory and percent-encoded.</param>
/// <param name="Reference">The <c>extras.paradise</c> reference, or null for a GLB nothing has stamped yet.</param>
public readonly record struct GlbImageReference(int ImageIndex, string Uri, AssetReference? Reference);

/// <summary>
/// A GLB's texture references BY IDENTITY: <c>images[i].extras.paradise = { guid, path }</c>, the
/// same shape as every reference in a document, kept inside the file the DCC round-trips.
/// </summary>
/// <remarks>
/// <para>
/// The <c>uri</c> is what Blender and every other glTF reader follow, so it must stay true; the
/// stamp is what the pipeline follows, so a texture renamed outside <c>mv</c> still resolves and
/// the uri can be caught up to it (<see cref="FollowUris"/>) instead of re-exporting the mesh.
/// The path in the stamp is assets-relative like every other reference; the uri is glb-relative
/// because that is what glTF specifies, and the two are converted at this boundary only.
/// </para>
/// <para>
/// A DCC that does not know the block drops it on re-export; the watcher's reconcile puts it back
/// by resolving the uri to the sidecar beside the texture (<see cref="Stamp"/>), the way it mints
/// a missing sidecar. Embedded and <c>data:</c> images carry nothing: they are not files.
/// </para>
/// </remarks>
public static class GlbTextureReferences
{
    public const string ExtrasKey = "paradise";

    /// <summary>Every external image, stamped or not; empty for bytes that are not a GLB.</summary>
    public static IReadOnlyList<GlbImageReference> Read(byte[] glb)
    {
        ArgumentNullException.ThrowIfNull(glb);

        if (!GlbBinary.TryRead(glb, out var gltf, out _)) return [];
        var found = new List<GlbImageReference>();
        foreach (var (index, image, uri) in ExternalImages(gltf))
        {
            found.Add(new GlbImageReference(index, uri, ReadStamp(image)));
        }

        return found;
    }

    /// <summary>
    /// Stamps every external image that has no reference yet, through <paramref name="resolve"/>
    /// (the uri as written → the reference it names, or null to leave it unstamped). Bytes come back
    /// unchanged when nothing was stamped, so a caller can skip the write.
    /// </summary>
    public static byte[] Stamp(byte[] glb, Func<string, AssetReference?> resolve)
    {
        ArgumentNullException.ThrowIfNull(glb);
        ArgumentNullException.ThrowIfNull(resolve);

        if (!GlbBinary.TryRead(glb, out var gltf, out var bin)) return glb;
        var changed = false;
        foreach (var (_, image, uri) in ExternalImages(gltf))
        {
            if (ReadStamp(image) is not null) continue;
            if (resolve(uri) is not { } reference) continue;

            WriteStamp(image, reference);
            changed = true;
        }

        return changed ? GlbBinary.Write(gltf, bin) : glb;
    }

    /// <summary>
    /// Makes every stamped image's uri say where its guid lives now, from <paramref name="glbPath"/>:
    /// the texture moved, or the GLB itself did and every relative uri in it went stale at once.
    /// <paramref name="currentPath"/> answers with the assets-relative path the guid lives at now,
    /// or null to keep the stamp's path. Bytes come back unchanged when every uri already agrees.
    /// </summary>
    /// <param name="glbPath">Where this GLB sits under <c>assets/</c> NOW, since uris are relative to it.</param>
    public static byte[] FollowUris(byte[] glb, string glbPath, Func<AssetReference, string?> currentPath)
    {
        ArgumentNullException.ThrowIfNull(glb);
        ArgumentNullException.ThrowIfNull(glbPath);
        ArgumentNullException.ThrowIfNull(currentPath);

        if (!GlbBinary.TryRead(glb, out var gltf, out var bin)) return glb;
        var changed = false;
        foreach (var (_, image, uri) in ExternalImages(gltf))
        {
            if (ReadStamp(image) is not { } reference) continue;

            var current = currentPath(reference) ?? reference.Path;
            var expected = UriFor(glbPath, current);
            if (current == reference.Path && expected == uri) continue;

            image["uri"] = expected;
            WriteStamp(image, reference with { Path = current });
            changed = true;
        }

        return changed ? GlbBinary.Write(gltf, bin) : glb;
    }

    /// <summary>The assets-relative path a uri names, or null when it climbs out of <c>assets/</c>.</summary>
    public static string? AssetPathFor(string glbPath, string uri)
    {
        ArgumentNullException.ThrowIfNull(glbPath);
        ArgumentNullException.ThrowIfNull(uri);

        var segments = new List<string>(Directory(glbPath));
        foreach (var part in Uri.UnescapeDataString(uri).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (segments.Count == 0) return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(part);
        }

        return string.Join('/', segments);
    }

    /// <summary>The uri a GLB at <paramref name="glbPath"/> writes to name <paramref name="assetPath"/>, percent-encoded as glTF requires.</summary>
    public static string UriFor(string glbPath, string assetPath)
    {
        ArgumentNullException.ThrowIfNull(glbPath);
        ArgumentNullException.ThrowIfNull(assetPath);

        var from = Directory(glbPath);
        var to = assetPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var shared = 0;
        while (shared < from.Length && shared < to.Length - 1 && string.Equals(from[shared], to[shared], StringComparison.Ordinal)) shared++;

        var parts = Enumerable.Repeat("..", from.Length - shared).Concat(to.Skip(shared).Select(Uri.EscapeDataString));
        return string.Join('/', parts);
    }

    private static string[] Directory(string glbPath)
    {
        var parts = glbPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? [] : parts[..^1];
    }

    private static IEnumerable<(int Index, JsonObject Image, string Uri)> ExternalImages(JsonObject gltf)
    {
        if (gltf["images"] is not JsonArray images) yield break;
        for (var index = 0; index < images.Count; index++)
        {
            if (images[index] is not JsonObject image) continue;
            if (image["bufferView"] is not null) continue;
            if (image["uri"]?.GetValue<string>() is not { } uri) continue;
            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            yield return (index, image, uri);
        }
    }

    /// <summary>A stamp that is not reference-shaped reads as absent: re-stamping it is the repair.</summary>
    private static AssetReference? ReadStamp(JsonObject image)
    {
        if (image["extras"]?[ExtrasKey] is not JsonObject stamp) return null;
        if (stamp[AssetReferenceCodec.GuidKey]?.GetValue<string>() is not { } guidText) return null;
        if (stamp[AssetReferenceCodec.PathKey]?.GetValue<string>() is not { Length: > 0 } path) return null;
        if (!DocumentGuid.TryParse(guidText, out var guid) || guid == Guid.Empty) return null;
        return new AssetReference(guid, path);
    }

    private static void WriteStamp(JsonObject image, AssetReference reference)
    {
        if (image["extras"] is not JsonObject extras)
        {
            extras = [];
            image["extras"] = extras;
        }

        extras[ExtrasKey] = new JsonObject
        {
            [AssetReferenceCodec.GuidKey] = DocumentGuid.Format(reference.Guid),
            [AssetReferenceCodec.PathKey] = reference.Path,
        };
    }
}
