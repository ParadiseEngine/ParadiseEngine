using System.Text.Json.Nodes;

namespace Paradise.Assets.Pipeline;

/// <summary>One image a GLB carries in its BIN chunk, with what the pipeline decided about it.</summary>
/// <param name="SourceExtension"><c>.png</c> or <c>.jpg</c> for an image that needs encoding; <see langword="null"/> when <see cref="IsKtx2"/>.</param>
/// <param name="PresetNote">Set when the preset was inferred from the name rather than a material slot — said out loud, because a wrong guess is a colour-space bug with no other symptom.</param>
public readonly record struct EmbeddedImage(
    int Index,
    byte[] Bytes,
    string? SourceExtension,
    TextureEncodingPreset Preset,
    string SidecarName,
    string? PresetNote)
{
    public bool IsKtx2 => SourceExtension is null;
}

/// <summary>
/// The GLB rewrites, bytes in and bytes out: list what is embedded, then either externalise every
/// embedded image to a <c>&lt;stem&gt;_&lt;i&gt;.ktx2</c> beside the mesh or replace it in the BIN
/// with KTX2. Encoding is the caller's (it owns the tool, the cache and the output tree), which
/// is what lets the build run this over a Zio mount (issue #212).
/// </summary>
public static class GlbTextureRewriter
{
    public const string Ktx2MimeType = "image/ktx2";
    public const string BasisuExtensionName = "KHR_texture_basisu";

    public static string SidecarName(string stem, int imageIndex) => $"{stem}_{imageIndex}.ktx2";

    /// <summary>Every image stored in the BIN chunk; empty for a GLB whose images are all external. False only for a GLB that cannot be parsed or that embeds an image of an unknown kind.</summary>
    public static bool TryListEmbedded(byte[] glb, string stem, out IReadOnlyList<EmbeddedImage> images, out string error)
    {
        ArgumentNullException.ThrowIfNull(glb);
        ArgumentNullException.ThrowIfNull(stem);

        images = [];
        error = "";
        if (!GlbBinary.TryRead(glb, out var gltf, out var bin))
        {
            error = "is not a readable GLB";
            return false;
        }

        if (gltf["images"] is not JsonArray imageNodes || gltf["bufferViews"] is not JsonArray bufferViews) return true;

        var presets = TextureEncodePolicy.MaterialPresets(gltf);
        var found = new List<EmbeddedImage>();
        for (var index = 0; index < imageNodes.Count; index++)
        {
            if (imageNodes[index] is not JsonObject image || image["bufferView"] is null) continue;

            if (!TryBufferViewBytes(bufferViews, image["bufferView"]!.GetValue<int>(), bin, out var bytes))
            {
                error = $"image #{index} points at a buffer view outside the BIN chunk";
                return false;
            }

            var mimeType = image["mimeType"]?.GetValue<string>() ?? "";
            string? extension;
            if (Ktx2Header.IsKtx2(bytes)) extension = null;
            else if (string.Equals(mimeType, "image/png", StringComparison.OrdinalIgnoreCase)) extension = ".png";
            else if (string.Equals(mimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase)) extension = ".jpg";
            else
            {
                error = $"image #{index} is '{mimeType}', which is neither KTX2 nor PNG/JPEG";
                return false;
            }

            string? note = null;
            if (!presets.TryGetValue(index, out var preset))
            {
                preset = TextureEncodePolicy.PresetFromImageName(image);
                note = $"texture #{index} '{image["name"]?.GetValue<string>()}' is bound to no material slot; preset {preset} inferred from its name";
            }

            found.Add(new EmbeddedImage(index, bytes, extension, preset, SidecarName(stem, index), note));
        }

        images = found;
        return true;
    }

    /// <summary>
    /// Points every embedded image at its sidecar and drops its bytes from the BIN, so the chunk
    /// holds geometry only. <paramref name="declareBasisu"/> adds <c>KHR_texture_basisu</c> for
    /// the images in <paramref name="transcoded"/>, which spec-conformant readers need and
    /// Paradise's own does not (issue #207: the build writes the same uri-and-mime contract as
    /// <see cref="MeshTextureReferences"/>). Idempotent: nothing embedded, nothing changed.
    /// </summary>
    public static bool TryExternalize(
        byte[] glb,
        IReadOnlyList<EmbeddedImage> images,
        bool declareBasisu,
        IReadOnlySet<int> transcoded,
        out byte[] rewritten,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(glb);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(transcoded);

        rewritten = glb;
        error = "";
        if (images.Count == 0) return true;

        if (!GlbBinary.TryRead(glb, out var gltf, out var bin)
            || gltf["images"] is not JsonArray imageNodes
            || gltf["bufferViews"] is not JsonArray bufferViews)
        {
            error = "is not a readable GLB";
            return false;
        }

        var dropped = new HashSet<int>();
        foreach (var embedded in images)
        {
            var image = (JsonObject)imageNodes[embedded.Index]!;
            dropped.Add(image["bufferView"]!.GetValue<int>());
            image.Remove("bufferView");
            image["uri"] = embedded.SidecarName;
            image["mimeType"] = Ktx2MimeType;
            image["name"] = Ktx2ImageName(image, embedded.Index);
        }

        if (declareBasisu && transcoded.Count > 0 && gltf["textures"] is JsonArray textures)
        {
            DeclareBasisu(gltf, textures, transcoded);
        }

        bin = RepackDropping(gltf, bufferViews, bin, dropped);
        SetFirstBufferLength(gltf, bin.Length);
        rewritten = GlbBinary.Write(gltf, bin);
        return true;
    }

    /// <summary>Replaces each listed image's BIN bytes with its KTX2 and declares <c>KHR_texture_basisu</c> on the textures that use it; the Godot host's in-place form.</summary>
    public static bool TryEmbedKtx2(
        byte[] glb,
        IReadOnlyDictionary<int, byte[]> ktx2ByImage,
        out byte[] rewritten,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(glb);
        ArgumentNullException.ThrowIfNull(ktx2ByImage);

        rewritten = glb;
        error = "";
        if (ktx2ByImage.Count == 0) return true;

        if (!GlbBinary.TryRead(glb, out var gltf, out var bin)
            || gltf["images"] is not JsonArray imageNodes
            || gltf["bufferViews"] is not JsonArray bufferViews)
        {
            error = "is not a readable GLB";
            return false;
        }

        var replacements = new Dictionary<int, byte[]>();
        foreach (var (index, ktx2) in ktx2ByImage)
        {
            if (index < 0 || index >= imageNodes.Count || imageNodes[index] is not JsonObject image || image["bufferView"] is null)
            {
                error = $"image #{index} is not an embedded image";
                return false;
            }

            replacements[image["bufferView"]!.GetValue<int>()] = ktx2;
            image["name"] = Ktx2ImageName(image, index);
            image["mimeType"] = Ktx2MimeType;
            image.Remove("uri");
        }

        bin = RepackReplacing(bufferViews, bin, replacements);
        if (gltf["textures"] is JsonArray textures) DeclareBasisu(gltf, textures, ktx2ByImage.Keys.ToHashSet());
        SetFirstBufferLength(gltf, bin.Length);
        rewritten = GlbBinary.Write(gltf, bin);
        return true;
    }

    private static bool TryBufferViewBytes(JsonArray bufferViews, int bufferViewIndex, byte[] bin, out byte[] bytes)
    {
        bytes = [];
        if (bufferViewIndex < 0 || bufferViewIndex >= bufferViews.Count) return false;
        if (bufferViews[bufferViewIndex] is not JsonObject bufferView || (bufferView["buffer"]?.GetValue<int>() ?? 0) != 0) return false;

        var byteOffset = bufferView["byteOffset"]?.GetValue<int>() ?? 0;
        var byteLength = bufferView["byteLength"]?.GetValue<int>() ?? 0;
        if (byteOffset < 0 || byteLength <= 0 || byteOffset + byteLength > bin.Length) return false;

        bytes = bin.AsSpan(byteOffset, byteLength).ToArray();
        return true;
    }

    private static byte[] RepackReplacing(JsonArray bufferViews, byte[] sourceBin, IReadOnlyDictionary<int, byte[]> replacements)
    {
        using var rebuilt = new MemoryStream();
        for (var i = 0; i < bufferViews.Count; i++)
        {
            if (bufferViews[i] is not JsonObject bufferView || (bufferView["buffer"]?.GetValue<int>() ?? 0) != 0) continue;

            var sourceOffset = bufferView["byteOffset"]?.GetValue<int>() ?? 0;
            var sourceLength = bufferView["byteLength"]?.GetValue<int>() ?? 0;
            if (sourceOffset < 0 || sourceLength <= 0 || sourceOffset + sourceLength > sourceBin.Length) continue;

            GlbBinary.WritePadding(rebuilt, 0x00);
            var bytes = replacements.TryGetValue(i, out var replacement)
                ? replacement
                : sourceBin.AsSpan(sourceOffset, sourceLength).ToArray();

            bufferView["byteOffset"] = (int)rebuilt.Position;
            bufferView["byteLength"] = bytes.Length;
            rebuilt.Write(bytes, 0, bytes.Length);
        }

        GlbBinary.WritePadding(rebuilt, 0x00);
        return rebuilt.ToArray();
    }

    private static byte[] RepackDropping(JsonObject gltf, JsonArray bufferViews, byte[] sourceBin, ISet<int> droppedViews)
    {
        var kept = Enumerable.Range(0, bufferViews.Count).Where(i => !droppedViews.Contains(i)).ToList();
        var newOffset = new Dictionary<int, int>();
        var newLength = new Dictionary<int, int>();
        byte[] newBin;
        using (var rebuilt = new MemoryStream())
        {
            foreach (var i in kept)
            {
                if (bufferViews[i] is not JsonObject bufferView) continue;

                if ((bufferView["buffer"]?.GetValue<int>() ?? 0) != 0)
                {
                    newOffset[i] = bufferView["byteOffset"]?.GetValue<int>() ?? 0;
                    newLength[i] = bufferView["byteLength"]?.GetValue<int>() ?? 0;
                    continue;
                }

                var offset = bufferView["byteOffset"]?.GetValue<int>() ?? 0;
                var length = bufferView["byteLength"]?.GetValue<int>() ?? 0;
                GlbBinary.WritePadding(rebuilt, 0x00);
                newOffset[i] = (int)rebuilt.Position;
                newLength[i] = length;
                if (length > 0 && offset >= 0 && offset + length <= sourceBin.Length) rebuilt.Write(sourceBin, offset, length);
            }

            GlbBinary.WritePadding(rebuilt, 0x00);
            newBin = rebuilt.ToArray();
        }

        var remap = new Dictionary<int, int>();
        var newViews = new JsonArray();
        for (var n = 0; n < kept.Count; n++)
        {
            var oldIndex = kept[n];
            remap[oldIndex] = n;
            var bufferView = (JsonObject)bufferViews[oldIndex]!.DeepClone();
            if ((bufferView["buffer"]?.GetValue<int>() ?? 0) == 0)
            {
                bufferView["byteOffset"] = newOffset[oldIndex];
                bufferView["byteLength"] = newLength[oldIndex];
            }

            // Cast to JsonNode: the Add<T> generic overload is AOT-unsafe (IL2026/IL3050).
            newViews.Add((JsonNode)bufferView);
        }

        gltf["bufferViews"] = newViews;

        if (gltf["accessors"] is JsonArray accessors)
        {
            foreach (var accessor in accessors.OfType<JsonObject>())
            {
                Remap(accessor, remap);
                if (accessor["sparse"] is JsonObject sparse)
                {
                    if (sparse["indices"] is JsonObject indices) Remap(indices, remap);
                    if (sparse["values"] is JsonObject values) Remap(values, remap);
                }
            }
        }

        foreach (var image in ((JsonArray)gltf["images"]!).OfType<JsonObject>())
        {
            if (image["bufferView"] is not null) Remap(image, remap);
        }

        return newBin;
    }

    private static void Remap(JsonObject node, IReadOnlyDictionary<int, int> remap)
    {
        if (node["bufferView"] is JsonValue value && value.TryGetValue(out int old) && remap.TryGetValue(old, out var updated))
        {
            node["bufferView"] = updated;
        }
    }

    private static void DeclareBasisu(JsonObject gltf, JsonArray textures, IReadOnlySet<int> ktx2Images)
    {
        foreach (var texture in textures.OfType<JsonObject>())
        {
            if (texture["source"] is null) continue;

            var source = texture["source"]!.GetValue<int>();
            if (!ktx2Images.Contains(source)) continue;

            if (texture["extensions"] is not JsonObject extensions)
            {
                extensions = new JsonObject();
                texture["extensions"] = extensions;
            }

            extensions[BasisuExtensionName] = new JsonObject { ["source"] = source };
            texture.Remove("source");
        }

        AddExtensionName(gltf, "extensionsUsed");
        AddExtensionName(gltf, "extensionsRequired");
    }

    private static void AddExtensionName(JsonObject gltf, string propertyName)
    {
        if (gltf[propertyName] is not JsonArray extensions)
        {
            extensions = new JsonArray();
            gltf[propertyName] = extensions;
        }

        // Match by value only on string entries — GetValue<string>() throws on non-string nodes,
        // and a malformed GLB may carry numeric/object entries in extensionsUsed/Required.
        if (!extensions.Any(n => n is JsonValue v && v.TryGetValue(out string? s) && string.Equals(s, BasisuExtensionName, StringComparison.Ordinal)))
        {
            extensions.Add((JsonNode)BasisuExtensionName);
        }
    }

    private static void SetFirstBufferLength(JsonObject gltf, int byteLength)
    {
        if (gltf["buffers"] is JsonArray { Count: > 0 } buffers && buffers[0] is JsonObject buffer)
        {
            buffer["byteLength"] = byteLength;
            return;
        }

        gltf["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = byteLength });
    }

    private static string Ktx2ImageName(JsonObject image, int index)
    {
        var sourceName = Path.GetFileNameWithoutExtension(image["name"]?.GetValue<string>() ?? $"Texture_{index}");
        return $"{sourceName}_KTX2.ktx2";
    }
}
