using System.Text.Json.Nodes;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>One external file a mesh container names: where in the container, and the uri it spells.</summary>
public readonly record struct ContainerReference(string Slot, string Uri);

/// <summary>
/// What the pipeline asks of a mesh container's bytes: which external files it names, and —
/// where the format allows it — spelling a new uri for one. Identity is never in here; that is
/// the sidecar's (<see cref="MeshImportSettings"/>), so a format that cannot be edited (FBX)
/// needs only the reading half.
/// </summary>
public static class MeshContainer
{
    public static bool IsMesh(UPath path)
        => string.Equals(path.GetExtensionWithDot(), ".glb", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <see cref="RewriteUris"/> can write this container. Only the uri the DCC follows depends on it; the pipeline resolves by identity either way.</summary>
    public static bool CanRewrite(UPath path) => IsMesh(path);

    /// <summary>Every external file the container names, in container order; empty for bytes that are not a container this reads.</summary>
    public static IReadOnlyList<ContainerReference> Read(UPath path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return IsMesh(path) ? ReadGlb(bytes) : [];
    }

    /// <summary>The container with each listed slot spelling its new uri; the input bytes when nothing changed or the format cannot be written.</summary>
    public static byte[] RewriteUris(UPath path, byte[] bytes, IReadOnlyDictionary<string, string> uriBySlot)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(uriBySlot);
        return CanRewrite(path) ? RewriteGlbUris(bytes, uriBySlot) : bytes;
    }

    /// <summary>The assets-relative path a container-relative uri names, or null when it climbs out of <c>assets/</c>.</summary>
    public static string? AssetPathFor(string containerPath, string uri)
    {
        ArgumentNullException.ThrowIfNull(containerPath);
        ArgumentNullException.ThrowIfNull(uri);

        var segments = new List<string>(Directory(containerPath));
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

    /// <summary>The uri a container at <paramref name="containerPath"/> writes to name <paramref name="assetPath"/>, percent-encoded as glTF requires.</summary>
    public static string UriFor(string containerPath, string assetPath)
    {
        ArgumentNullException.ThrowIfNull(containerPath);
        ArgumentNullException.ThrowIfNull(assetPath);

        var from = Directory(containerPath);
        var to = assetPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var shared = 0;
        while (shared < from.Length && shared < to.Length - 1 && string.Equals(from[shared], to[shared], StringComparison.Ordinal)) shared++;

        var parts = Enumerable.Repeat("..", from.Length - shared).Concat(to.Skip(shared).Select(Uri.EscapeDataString));
        return string.Join('/', parts);
    }

    private static string[] Directory(string containerPath)
    {
        var parts = containerPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? [] : parts[..^1];
    }

    // ---- glTF binary --------------------------------------------------------------------------

    private static IReadOnlyList<ContainerReference> ReadGlb(byte[] glb)
    {
        if (!GlbBinary.TryRead(glb, out var gltf, out _)) return [];
        return ExternalImages(gltf).Select(image => new ContainerReference(Slot(image.Index), image.Uri)).ToList();
    }

    private static byte[] RewriteGlbUris(byte[] glb, IReadOnlyDictionary<string, string> uriBySlot)
    {
        if (!GlbBinary.TryRead(glb, out var gltf, out var bin)) return glb;
        var changed = false;
        foreach (var (index, image, uri) in ExternalImages(gltf))
        {
            if (!uriBySlot.TryGetValue(Slot(index), out var expected) || expected == uri) continue;
            image["uri"] = expected;
            changed = true;
        }

        return changed ? GlbBinary.Write(gltf, bin) : glb;
    }

    private static string Slot(int imageIndex) => $"images[{imageIndex}]";

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
}
