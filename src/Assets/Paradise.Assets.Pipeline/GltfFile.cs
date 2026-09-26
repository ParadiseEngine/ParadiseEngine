using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>A <c>.gltf</c> model source as the GLB the pipeline reads, and a rewritten GLB put back into the <c>.gltf</c> and its buffer.</summary>
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
/// Writing keeps the file a <c>.gltf</c>: the JSON is rewritten and the BIN goes back to the one
/// buffer's uri — a <c>data:</c> buffer stays one, and a buffer file is written only when its
/// bytes changed. A <c>.gltf</c> with more than one buffer is refused: a rewrite cannot tell which
/// file each byte belongs to. The Blender addon reads and writes the same shape.
/// </para>
/// </remarks>
internal static class GltfFile
{
    private const string DataScheme = "data:";

    private static readonly JsonWriterOptions s_writerOptions = new()
    {
        Indented = true,
        // Not HTML: '+' in a base64 data uri and non-ASCII names stay readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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

    /// <summary>The document as a <c>.gltf</c> file's bytes: indented UTF-8 JSON.</summary>
    public static byte[] Serialize(JsonObject gltf)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, s_writerOptions))
        {
            gltf.WriteTo(writer);
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    /// <summary>The GLB the <c>.gltf</c> at <paramref name="path"/> is, built in memory.</summary>
    /// <exception cref="InvalidDataException">The JSON, a buffer or a <c>data:</c> image cannot be read.</exception>
    public static byte[] ReadGlb(IFileSystem fileSystem, UPath path)
    {
        if (!TryParse(fileSystem.ReadAllBytes(path), out var gltf)) throw new InvalidDataException("is not a readable glTF JSON document");

        var buffers = gltf["buffers"] as JsonArray ?? [];
        using var bin = new MemoryStream();
        var starts = new int[buffers.Count];
        for (var i = 0; i < buffers.Count; i++)
        {
            var bytes = BufferBytes(fileSystem, path, buffers[i] as JsonObject, i);
            GlbBinary.WritePadding(bin, 0x00);
            starts[i] = (int)bin.Position;
            bin.Write(bytes);
        }

        var views = gltf["bufferViews"] as JsonArray ?? [];
        for (var i = 0; i < views.Count; i++)
        {
            if (views[i] is not JsonObject view) throw new InvalidDataException($"buffer view #{i} is not an object");
            var buffer = Int(view["buffer"]) ?? -1;
            if (buffer < 0 || buffer >= starts.Length) throw new InvalidDataException($"buffer view #{i} names buffer #{buffer}, which the file does not declare");
            view["buffer"] = 0;
            view["byteOffset"] = starts[buffer] + (Int(view["byteOffset"]) ?? 0);
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

    /// <summary>Puts <paramref name="glb"/> back into the <c>.gltf</c> at <paramref name="path"/>: its JSON, and its buffer when the bytes it holds changed.</summary>
    /// <exception cref="InvalidDataException">The GLB or the <c>.gltf</c> cannot be read, the file names more than one buffer, or its buffer uri names no file this may write.</exception>
    public static void WriteGlb(IFileSystem fileSystem, UPath path, byte[] glb)
    {
        if (!GlbBinary.TryRead(glb, out var gltf, out var bin)) throw new InvalidDataException("is not a readable GLB");
        if (!TryParse(fileSystem.ReadAllBytes(path), out var original)) throw new InvalidDataException("is not a readable glTF JSON document");

        var originalBuffers = original["buffers"] as JsonArray ?? [];
        if (originalBuffers.Count > 1)
        {
            throw new InvalidDataException($"names {originalBuffers.Count} buffers, and a .gltf is only rewritten with one; export it with a single .bin");
        }

        var length = gltf["buffers"] is JsonArray { Count: > 0 } buffers && buffers[0] is JsonObject declared
            ? Math.Clamp(Int(declared["byteLength"]) ?? bin.Length, 0, bin.Length)
            : 0;

        if (length == 0)
        {
            gltf.Remove("buffers");
        }
        else
        {
            var bytes = length == bin.Length ? bin : bin[..length];
            var uri = originalBuffers.Count == 1 && originalBuffers[0] is JsonObject kept && Text(kept["uri"]) is { } keptUri
                ? keptUri
                : Uri.EscapeDataString(Path.GetFileNameWithoutExtension(path.GetName()) + ".bin");

            if (IsData(uri))
            {
                uri = $"data:application/octet-stream;base64,{Convert.ToBase64String(bytes)}";
            }
            else
            {
                var target = Resolve(fileSystem, path, uri) ?? throw new InvalidDataException($"buffer uri '{uri}' names no file under assets/");
                if (!fileSystem.FileExists(target) || !fileSystem.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) fileSystem.WriteAllBytes(target, bytes);
            }

            gltf["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = length, ["uri"] = uri });
        }

        fileSystem.WriteAllBytes(path, Serialize(gltf));
    }

    /// <summary>
    /// The file a container-relative uri names: percent-decoded and resolved against the
    /// <c>.gltf</c>'s directory, confined to <c>assets/</c> (to the file's own directory outside
    /// a project); null for a uri that leaves it, or is absolute or remote.
    /// </summary>
    internal static UPath? Resolve(IFileSystem fileSystem, UPath gltf, string uri)
    {
        var directory = gltf.GetDirectory();
        var root = AssetProjectLayout.TryLocate(fileSystem, directory, out var layout) && gltf.IsInDirectory(layout!.Assets, recursive: true)
            ? layout.Assets
            : directory;
        var container = root == UPath.Root ? gltf.FullName[1..] : gltf.FullName[(root.FullName.Length + 1)..];
        return MeshContainer.AssetPathFor(container, uri) is { Length: > 0 } relative ? (root / relative).ToAbsolute() : (UPath?)null;
    }

    private static byte[] BufferBytes(IFileSystem fileSystem, UPath path, JsonObject? buffer, int index)
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
            var file = Resolve(fileSystem, path, uri) ?? throw new InvalidDataException($"buffer #{index} uri '{uri}' leaves assets/ or is not a relative file");
            if (!fileSystem.FileExists(file)) throw new InvalidDataException($"buffer #{index} names '{uri}', which does not exist");
            bytes = fileSystem.ReadAllBytes(file);
        }

        if (bytes.Length < length) throw new InvalidDataException($"buffer #{index} ('{Shorten(uri)}') holds {bytes.Length} bytes but declares {length}");
        return bytes.Length == length ? bytes : bytes[..length];
    }

    private static bool IsData(string uri) => uri.StartsWith(DataScheme, StringComparison.OrdinalIgnoreCase);

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
