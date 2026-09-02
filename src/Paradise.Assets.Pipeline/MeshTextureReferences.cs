using System.Text.Json.Nodes;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// Repoints a GLB's external PNG/JPEG references to the KTX2 the texture step writes at the same
/// relative place. URI and MIME type only, no <c>KHR_texture_basisu</c>: Paradise's reader
/// resolves on <c>uri</c> + <c>mimeType</c>, which makes the output spec-invalid glTF for other
/// readers — issue #207. Policy-free: what a missing source means is the runner's call.
/// </summary>
public static class MeshTextureReferences
{
    public const string Ktx2MimeType = "image/ktx2";

    private static readonly string[] s_encodedExtensions = [".png", ".jpg", ".jpeg"];

    /// <summary><paramref name="Glb"/> is the input unchanged when nothing needed repointing.</summary>
    public readonly record struct MeshRewrite(byte[] Glb, IReadOnlyList<string> Sources);

    /// <summary>Idempotent: <c>.ktx2</c>, embedded, and uri-less images are left as they are.</summary>
    public static MeshRewrite Rewrite(byte[] glb)
    {
        ArgumentNullException.ThrowIfNull(glb);

        if (!GlbBinary.TryRead(glb, out var gltf, out var binChunk)) return new MeshRewrite(glb, []);
        if (gltf["images"] is not JsonArray images) return new MeshRewrite(glb, []);

        var sources = new List<string>();
        foreach (var node in images)
        {
            if (node is not JsonObject image) continue;
            if (image["bufferView"] is not null) continue;
            if (image["uri"]?.GetValue<string>() is not { } uri) continue;
            if (!IsEncodedImage(uri)) continue;

            sources.Add(uri);
            image["uri"] = Path.ChangeExtension(uri, ".ktx2");
            image["mimeType"] = Ktx2MimeType;
        }

        return sources.Count == 0
            ? new MeshRewrite(glb, [])
            : new MeshRewrite(GlbBinary.Write(gltf, binChunk), sources);
    }

    private static bool IsEncodedImage(string uri)
    {
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;

        var extension = Path.GetExtension(uri);
        return s_encodedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}
