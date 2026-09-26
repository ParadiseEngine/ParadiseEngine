using System.Text.Json.Nodes;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>One external file a mesh container names: where in the container, and the uri it spells.</summary>
public readonly record struct ContainerReference(string Slot, string Uri);

/// <summary>
/// What the pipeline asks of a model source's own bytes — a GLB, or a <c>.gltf</c>'s JSON: which
/// external files it names, and spelling a new uri for one. Identity is never in here; that is the
/// sidecar's (<see cref="GlbImportSettings"/>). A converted source (<see cref="ModelSource.IsConverted"/>)
/// names no files to the pipeline — its GLB embeds every image — and is never written.
/// </summary>
public static class MeshContainer
{
    private static readonly char[] s_separators = ['/', '\\'];

    /// <summary>Whether <see cref="RewriteUris"/> can write this container. Only the uri the DCC follows depends on it; the pipeline resolves by identity either way.</summary>
    public static bool CanRewrite(UPath path) => IsGlb(path) || GltfFile.Is(path);

    /// <summary>Every external file the container names, in container order; empty for bytes that are not a container this reads.</summary>
    public static IReadOnlyList<ContainerReference> Read(UPath path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return TryDocument(path, bytes, out var gltf, out _) ? References(gltf) : [];
    }

    /// <summary>Every external file the container at <paramref name="path"/> names; empty, without reading it, for a format that names none.</summary>
    public static IReadOnlyList<ContainerReference> Read(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return CanRewrite(path) ? Read(path, fileSystem.ReadAllBytes(path)) : [];
    }

    /// <summary>The container with each listed slot spelling its new uri; the input bytes when nothing changed or the format cannot be written. A <c>.gltf</c> stays JSON.</summary>
    public static byte[] RewriteUris(UPath path, byte[] bytes, IReadOnlyDictionary<string, string> uriBySlot)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(uriBySlot);
        if (!TryDocument(path, bytes, out var gltf, out var bin)) return bytes;

        var changed = false;
        foreach (var (index, image, uri) in ExternalImages(gltf))
        {
            if (!uriBySlot.TryGetValue(Slot(index), out var expected) || expected == uri) continue;
            image["uri"] = expected;
            changed = true;
        }

        if (!changed) return bytes;
        return GltfFile.Is(path) ? GltfFile.Serialize(gltf) : GlbBinary.Write(gltf, bin);
    }

    /// <summary>The external images a GLB names: the GLB <see cref="ModelSource.ReadGlb"/> made of any model source, whose image slots are the source's own.</summary>
    internal static IReadOnlyList<ContainerReference> ReadGlb(byte[] glb)
        => GlbBinary.TryRead(glb, out var gltf, out _) ? References(gltf) : [];

    /// <summary>The assets-relative path a container-relative uri names, percent-decoded; null when it climbs out of <c>assets/</c> or is absolute or remote.</summary>
    public static string? AssetPathFor(string containerPath, string uri)
    {
        ArgumentNullException.ThrowIfNull(containerPath);
        ArgumentNullException.ThrowIfNull(uri);

        var decoded = Uri.UnescapeDataString(uri);
        if (IsAbsolute(decoded)) return null;

        var segments = new List<string>(Directory(containerPath));
        foreach (var part in decoded.Split(s_separators, StringSplitOptions.RemoveEmptyEntries))
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

    /// <summary>Whether the GLB declares anything to extract: geometry, or a rig or clip on its own (an animation-only file). A GLB of images alone has nothing.</summary>
    public static bool HasParts(byte[] glb)
    {
        ArgumentNullException.ThrowIfNull(glb);
        if (!GlbBinary.TryRead(glb, out var gltf, out _)) return false;
        return NonEmpty(gltf, "meshes") || NonEmpty(gltf, "skins") || NonEmpty(gltf, "animations");
    }

    private static bool NonEmpty(JsonObject gltf, string key) => gltf[key] is JsonArray array && array.Count > 0;

    /// <summary>Whether two uris name the same file: a DCC may write <c>a b.png</c> where glTF says <c>a%20b.png</c>, and that is not a move.</summary>
    public static bool SameUri(string left, string right)
        => string.Equals(Uri.UnescapeDataString(left), Uri.UnescapeDataString(right), StringComparison.Ordinal);

    private static bool IsGlb(UPath path)
        => string.Equals(path.GetExtensionWithDot(), ".glb", StringComparison.OrdinalIgnoreCase);

    /// <summary>Rooted, or with a scheme (a drive letter included): a container names files relative to itself, never a remote or host path.</summary>
    private static bool IsAbsolute(string uri)
    {
        if (uri.Length == 0) return false;
        if (uri[0] is '/' or '\\') return true;

        var colon = uri.IndexOf(':');
        return colon > 0 && uri.IndexOfAny(s_separators) is var separator && (separator < 0 || colon < separator);
    }

    private static string[] Directory(string containerPath)
    {
        var parts = containerPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? [] : parts[..^1];
    }

    private static bool TryDocument(UPath path, byte[] bytes, out JsonObject gltf, out byte[] bin)
    {
        bin = [];
        if (IsGlb(path)) return GlbBinary.TryRead(bytes, out gltf, out bin);

        gltf = new JsonObject();
        return GltfFile.Is(path) && GltfFile.TryParse(bytes, out gltf);
    }

    private static List<ContainerReference> References(JsonObject gltf)
        => [.. ExternalImages(gltf).Select(image => new ContainerReference(Slot(image.Index), image.Uri))];

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
