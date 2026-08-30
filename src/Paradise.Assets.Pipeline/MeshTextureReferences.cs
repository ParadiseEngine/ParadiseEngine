using System.Text.Json.Nodes;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// One GLB's external texture references, repointed from their PNG/JPEG sources to the KTX2 the
/// texture step produces.
/// </summary>
/// <remarks>
/// <para>
/// A source GLB references its textures the way an author has them: <c>../textures/rust.png</c>,
/// a real file under <c>assets/</c>. The build compiles that PNG to <c>../textures/rust.ktx2</c>
/// at the same relative place, so the copied-through mesh has to say so — otherwise the output
/// tree ships a mesh pointing at a file the build never wrote, and the failure surfaces as a
/// missing texture in a renderer log rather than as anything the build could name.
/// </para>
/// <para>
/// The rewrite is <b>URI and MIME type only</b>. Paradise's own reader resolves external KTX2
/// through plain <c>images[].uri</c> plus <c>mimeType</c>, so there is no
/// <c>KHR_texture_basisu</c> to add and no <c>extensionsUsed</c> to maintain — adding either
/// would be inventing a contract neither side reads.
/// </para>
/// <para>
/// Deliberately <b>policy-free</b>: it reports which sources it repointed and never decides what
/// a missing one means. Whether a dangling reference fails the build belongs to
/// <see cref="BuildRunner"/>, which knows where <c>assets/</c> is.
/// </para>
/// </remarks>
public static class MeshTextureReferences
{
    /// <summary>The MIME type an external KTX2 image carries.</summary>
    public const string Ktx2MimeType = "image/ktx2";

    private static readonly string[] s_encodedExtensions = [".png", ".jpg", ".jpeg"];

    /// <summary>What <see cref="Rewrite"/> did to one mesh.</summary>
    /// <param name="Glb">
    /// The mesh, rewritten — or the input unchanged when there was nothing to repoint, so a
    /// caller never needs to branch on whether work happened.
    /// </param>
    /// <param name="Sources">
    /// The source URIs that were repointed, exactly as the GLB spelled them (e.g.
    /// <c>../textures/rust.png</c>), relative to the GLB's own directory. Empty when the mesh has
    /// no external encoded images.
    /// </param>
    public readonly record struct MeshRewrite(byte[] Glb, IReadOnlyList<string> Sources);

    /// <summary>
    /// Repoints every external PNG/JPEG image reference in <paramref name="glb"/> at the KTX2 the
    /// texture step writes beside it.
    /// </summary>
    /// <remarks>
    /// Idempotent: an image already naming a <c>.ktx2</c>, one backed by a <c>bufferView</c>
    /// (embedded — a different step's problem, and refused upstream), and one with no <c>uri</c>
    /// at all are each left exactly as they are. Re-running therefore reports no sources and
    /// returns the input.
    /// </remarks>
    /// <param name="glb">The mesh bytes.</param>
    /// <returns>The rewritten mesh and the sources it repointed.</returns>
    public static MeshRewrite Rewrite(byte[] glb)
    {
        ArgumentNullException.ThrowIfNull(glb);

        // Unreadable is not this type's error to raise: the caller has the path and the context to
        // say which file, and a GLB the container cannot parse fails the same way it always did.
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

    /// <summary>
    /// Whether <paramref name="uri"/> names an image the texture step compiles.
    /// </summary>
    /// <remarks>
    /// A <c>data:</c> URI is excluded even when it ends in something that looks like one of these
    /// extensions: its bytes are in the document, so there is no source file to compile and no
    /// path to repoint.
    /// </remarks>
    private static bool IsEncodedImage(string uri)
    {
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;

        var extension = Path.GetExtension(uri);
        return s_encodedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}
