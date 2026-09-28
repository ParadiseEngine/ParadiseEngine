using System.Text.Json;
using System.Text.Json.Nodes;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>A <c>.gltf</c> model source as the GLB the pipeline reads.</summary>
/// <remarks>
/// <para>
/// A <c>.gltf</c> is the same asset as a GLB whose buffers live beside it. Reading concatenates
/// every buffer, four-byte aligned, into the one BIN chunk and re-offsets the buffer views; a
/// <c>data:</c> image moves into that chunk as a buffer view, which is what makes it an embedded
/// image to extraction. Image uris are kept, so they resolve against the <c>.gltf</c>'s path as a
/// GLB's resolve against its own. Buffers are read through the caller's file system, so a build
/// records a <c>.bin</c> as an input.
/// </para>
/// <para>
/// The file is never written, so a buffer file that moved is not followed by its uri. It is found
/// by the identity the sidecar records for its slot (<see cref="MeshReferences"/>) while the uri
/// still spells what was recorded, and by the uri itself when nothing is recorded yet or a
/// re-export changed it.
/// </para>
/// </remarks>
internal static class GltfFile
{
    private const string DataScheme = "data:";

    public static bool Is(UPath path)
        => string.Equals(path.GetExtensionWithDot(), ".gltf", StringComparison.OrdinalIgnoreCase);

    /// <summary>The document, or false for bytes that are not a JSON object.</summary>
    public static bool TryParse(byte[] json, out JsonObject gltf)
    {
        gltf = new JsonObject();
        try
        {
            if (JsonNode.Parse(json) is not JsonObject parsed) return false;
            gltf = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The GLB the <c>.gltf</c> at <paramref name="path"/> is, built in memory.</summary>
    /// <param name="index">The tree recorded buffer identities resolve against; null to look only where the sidecar last recorded each.</param>
    /// <exception cref="InvalidDataException">The JSON, a buffer or a <c>data:</c> image cannot be read, or a buffer view does not lie within its buffer.</exception>
    public static byte[] ReadGlb(IFileSystem fileSystem, UPath path, AssetIndex? index)
    {
        if (!TryParse(fileSystem.ReadAllBytes(path), out var gltf)) throw new InvalidDataException("is not a readable glTF JSON document");

        var recorded = GlbImportSettings.BySlot(MeshReferences.Recorded(fileSystem, path));
        var buffers = gltf["buffers"] as JsonArray ?? [];
        using var bin = new MemoryStream();
        var starts = new int[buffers.Count];
        var lengths = new int[buffers.Count];
        for (var i = 0; i < buffers.Count; i++)
        {
            var bytes = BufferBytes(fileSystem, path, buffers[i] as JsonObject, i, recorded, index);
            GlbBinary.WritePadding(bin, 0x00);
            starts[i] = (int)bin.Position;
            lengths[i] = bytes.Length;
            bin.Write(bytes);
        }

        var views = gltf["bufferViews"] as JsonArray ?? [];
        for (var i = 0; i < views.Count; i++)
        {
            if (views[i] is not JsonObject view) throw new InvalidDataException($"buffer view #{i} is not an object");
            var buffer = Int(view["buffer"]) ?? -1;
            if (buffer < 0 || buffer >= starts.Length) throw new InvalidDataException($"buffer view #{i} names buffer #{buffer}, which the file does not declare");
            var offset = Int(view["byteOffset"]) ?? 0;
            var length = Int(view["byteLength"]) ?? 0;
            // Checked per buffer: once concatenated, a view overrunning its buffer would read the
            // next one's bytes, which the GLB reader's bounds check cannot tell from its own.
            if (length < 1) throw new InvalidDataException($"buffer view #{i} declares no positive byteLength");
            if (offset < 0 || (long)offset + length > lengths[buffer])
            {
                throw new InvalidDataException($"buffer view #{i} spans bytes {offset} to {(long)offset + length} of buffer #{buffer}, which holds {lengths[buffer]}");
            }

            if (view["byteStride"] is { } stride && !IsStride(Int(stride)))
            {
                throw new InvalidDataException($"buffer view #{i} has byteStride {stride}; glTF allows a multiple of 4 from 4 to 252");
            }

            view["buffer"] = 0;
            view["byteOffset"] = starts[buffer] + offset;
        }

        var images = gltf["images"] as JsonArray ?? [];
        for (var i = 0; i < images.Count; i++)
        {
            if (images[i] is not JsonObject image || image["bufferView"] is not null) continue;
            if (Text(image["uri"]) is not { } uri || !IsData(uri)) continue;

            var bytes = DecodeData(uri, out var mediaType) ?? throw new InvalidDataException($"image #{i} is a data uri that is not base64");
            GlbBinary.WritePadding(bin, 0x00);
            views.Add((JsonNode)new JsonObject { ["buffer"] = 0, ["byteOffset"] = (int)bin.Position, ["byteLength"] = bytes.Length });
            bin.Write(bytes);
            if (views.Parent is null) gltf["bufferViews"] = views;
            image.Remove("uri");
            image["bufferView"] = views.Count - 1;
            if (image["mimeType"] is null && mediaType.Length > 0) image["mimeType"] = mediaType;
        }

        if (bin.Length > 0) gltf["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = (int)bin.Length });
        else gltf.Remove("buffers");

        return GlbBinary.Write(gltf, bin.ToArray());
    }

    /// <summary>
    /// The file a container-relative uri names: percent-decoded and resolved against the
    /// <c>.gltf</c>'s directory, confined to <c>assets/</c> (to the file's own directory outside
    /// a project); null for a uri that leaves it, or is absolute or remote.
    /// </summary>
    private static UPath? Resolve(IFileSystem fileSystem, UPath gltf, string uri)
    {
        var directory = gltf.GetDirectory();
        var root = AssetProjectLayout.TryLocate(fileSystem, directory, out var layout) && gltf.IsInDirectory(layout!.Assets, recursive: true)
            ? layout.Assets
            : directory;
        var container = root == UPath.Root ? gltf.FullName[1..] : gltf.FullName[(root.FullName.Length + 1)..];
        return MeshContainer.AssetPathFor(container, uri) is { Length: > 0 } relative ? (root / relative).ToAbsolute() : (UPath?)null;
    }

    /// <summary>
    /// Where the buffer the sidecar recorded is now: by its guid through <paramref name="index"/>,
    /// or with no tree at hand at the path last recorded while the sidecar there carries the guid;
    /// null when neither finds it.
    /// </summary>
    private static UPath? Identified(IFileSystem fileSystem, UPath gltf, AssetReference identity, AssetIndex? index)
    {
        if (index is not null)
        {
            var resolution = index.Resolve(identity);
            return resolution.Found ? resolution.Asset : (UPath?)null;
        }

        if (!AssetProjectLayout.TryLocate(fileSystem, gltf.GetDirectory(), out var layout)) return null;
        var hinted = (layout!.Assets / identity.Path).ToAbsolute();
        var sidecar = SidecarMeta.PathFor(hinted);
        if (!hinted.IsInDirectory(layout.Assets, recursive: true) || !fileSystem.FileExists(hinted) || !fileSystem.FileExists(sidecar)) return null;
        try
        {
            return SidecarMeta.Load(fileSystem, sidecar).Guid == identity.Guid ? hinted : (UPath?)null;
        }
        catch (SidecarMetaException)
        {
            return null;
        }
    }

    private static byte[] BufferBytes(IFileSystem fileSystem, UPath path, JsonObject? buffer, int index, IReadOnlyDictionary<string, MeshReference> recorded, AssetIndex? assets)
    {
        if (buffer is null) throw new InvalidDataException($"buffer #{index} is not an object");
        var length = Int(buffer["byteLength"]) ?? -1;
        if (length < 0) throw new InvalidDataException($"buffer #{index} declares no byteLength");
        if (Text(buffer["uri"]) is not { } uri) throw new InvalidDataException($"buffer #{index} has no uri; only a GLB's own buffer may omit it");

        byte[] bytes;
        if (IsData(uri))
        {
            bytes = DecodeData(uri, out _) ?? throw new InvalidDataException($"buffer #{index} is a data uri that is not base64");
        }
        else
        {
            // The recorded identity describes this slot only while the uri is the one it was
            // recorded from; a changed uri is a re-export, and names the file itself.
            var slot = $"buffers[{index}]";
            var identity = recorded.TryGetValue(slot, out var entry) && MeshContainer.SameUri(entry.Uri, uri) ? entry.Reference : null;
            var file = (identity is null ? null : Identified(fileSystem, path, identity, assets))
                ?? Resolve(fileSystem, path, uri)
                ?? throw new InvalidDataException($"{slot} uri '{uri}' leaves assets/ or is not a relative file");
            if (!fileSystem.FileExists(file))
            {
                throw new InvalidDataException(identity is null
                    ? $"{slot} names '{uri}', which does not exist"
                    : $"{slot} names '{uri}', which does not exist, and no asset carries guid {DocumentGuid.Format(identity.Guid)} its sidecar records for it");
            }

            bytes = fileSystem.ReadAllBytes(file);
        }

        if (bytes.Length < length) throw new InvalidDataException($"buffer #{index} ('{Shorten(uri)}') holds {bytes.Length} bytes but declares {length}");
        return bytes.Length == length ? bytes : bytes[..length];
    }

    private static bool IsData(string uri) => uri.StartsWith(DataScheme, StringComparison.OrdinalIgnoreCase);

    private static bool IsStride(int? stride) => stride is >= 4 and <= 252 && stride % 4 == 0;

    /// <summary>The payload of a base64 <c>data:</c> uri and its media type; null when it is not base64.</summary>
    private static byte[]? DecodeData(string uri, out string mediaType)
    {
        mediaType = "";
        var comma = uri.IndexOf(',');
        if (comma < 0) return null;

        var header = uri[DataScheme.Length..comma];
        if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return null;
        mediaType = header[..^";base64".Length].Split(';')[0];

        try
        {
            return Convert.FromBase64String(uri[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Shorten(string uri) => IsData(uri) ? "data:" : uri;

    private static int? Int(JsonNode? node) => node is JsonValue value && value.TryGetValue(out int result) ? result : null;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? result) ? result : null;
}
