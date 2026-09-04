using System.Text.Json.Nodes;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// Repoints a GLB's external PNG/JPEG references to the KTX2 the texture step writes at the same
/// relative place, declared through <c>KHR_texture_basisu</c> like every other KTX2 the pipeline
/// writes: <c>image/ktx2</c> is only valid under that extension, and a second contract for the
/// same output was what issue #207 found. A KTX2 is never authored — <c>verify</c> refuses one
/// under <c>assets/</c> — so a reference that already names one is not this pass's to touch.
/// Policy-free: what a missing source means is the runner's call.
/// </summary>
public static class MeshTextureReferences
{
    public const string Ktx2MimeType = "image/ktx2";

    private static readonly string[] s_encodedExtensions = [".png", ".jpg", ".jpeg"];

    /// <summary><paramref name="Glb"/> is the input unchanged when nothing needed repointing.</summary>
    public readonly record struct MeshRewrite(byte[] Glb, IReadOnlyList<string> Sources);

    /// <summary>Idempotent: embedded and uri-less images are left as they are, and so is a reference this pass already repointed.</summary>
    public static MeshRewrite Rewrite(byte[] glb)
    {
        ArgumentNullException.ThrowIfNull(glb);

        if (!GlbBinary.TryRead(glb, out var gltf, out var binChunk)) return new MeshRewrite(glb, []);
        if (gltf["images"] is not JsonArray images) return new MeshRewrite(glb, []);

        var sources = new List<string>();
        var ktx2 = new HashSet<int>();
        for (var index = 0; index < images.Count; index++)
        {
            if (images[index] is not JsonObject image) continue;
            if (image["bufferView"] is not null) continue;
            if (image["uri"]?.GetValue<string>() is not { } uri) continue;
            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            if (!IsEncodedImage(uri)) continue;

            sources.Add(uri);
            ktx2.Add(index);
            image["uri"] = Path.ChangeExtension(uri, ".ktx2");
            image["mimeType"] = Ktx2MimeType;
        }

        if (ktx2.Count == 0) return new MeshRewrite(glb, []);

        GlbTextureRewriter.DeclareBasisu(gltf, ktx2);
        return new MeshRewrite(GlbBinary.Write(gltf, binChunk), sources);
    }

    private static bool IsEncodedImage(string uri)
        => s_encodedExtensions.Contains(Path.GetExtension(uri), StringComparer.OrdinalIgnoreCase);
}
