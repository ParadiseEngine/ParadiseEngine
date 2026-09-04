using System.Text.Json.Nodes;

using Paradise.Assets.Documents;
using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// Writes a material document's glTF-expressible half back into the GLB's material of the same
/// index: the document is the authored source, and the DCC re-imports what the GLB says.
/// </summary>
/// <remarks>
/// Only what glTF can say: factors, alpha mode and cutoff, double-sided, emissive, transmission,
/// the five texture bindings and the base-colour uv transform. Paradise-only fields
/// (<c>MaterialKind</c>, <c>ColorA/B</c>, <c>EmissiveStrength</c>, <c>RenderQueue</c>, …) live in
/// the document alone; <see cref="Subset"/> is the list, and it is also what both sides'
/// fingerprints are taken over, so a Paradise-only edit never reads as a divergence. A texture
/// binding names an image by the uri the GLB would spell for it; an image the GLB does not have
/// yet is added with a texture entry, and an emptied slot drops the binding.
/// </remarks>
public static class GlbMaterialWriter
{
    /// <summary>The document keys glTF can express, in the order the extractor writes them; anything else is the document's alone.</summary>
    public static readonly IReadOnlyList<string> ExpressibleKeys =
    [
        "MetallicFactor", "RoughnessFactor", "NormalScale", "OcclusionStrength", "AlphaMode", "AlphaCutoff", "DoubleSided",
        "TransmissionFactor", "BaseColorUvOffset", "BaseColorUvScale", "BaseColorUvRotation",
        "BaseColorTexture", "MetallicRoughnessTexture", "NormalTexture", "OcclusionTexture", "EmissiveTexture",
        "BaseColorFactor", "EmissiveFactor",
    ];

    /// <summary>The document restricted to what glTF can express, in a fixed order: what a fingerprint is taken over.</summary>
    public static CanonicalTomlTable Subset(CanonicalTomlTable material)
    {
        ArgumentNullException.ThrowIfNull(material);
        var subset = new CanonicalTomlTable();
        foreach (var key in ExpressibleKeys)
        {
            if (material.Value(key) is { } value) subset.Add(key, value);
        }

        return subset;
    }

    /// <summary>The texture bindings a material document can carry, each the document key of an asset reference.</summary>
    private static readonly string[] s_textureKeys = ["BaseColorTexture", "MetallicRoughnessTexture", "NormalTexture", "OcclusionTexture", "EmissiveTexture"];

    private static readonly string[] s_authoredImageExtensions = [".png", ".jpg", ".jpeg"];

    /// <summary>The GLB with material <paramref name="materialIndex"/> rewritten from <paramref name="material"/>; the input bytes when nothing changed, or when <paramref name="problem"/> says why it was refused.</summary>
    /// <param name="glbPath">Where the GLB sits under <c>assets/</c>, since image uris are relative to it.</param>
    public static byte[] Write(byte[] glb, string glbPath, int materialIndex, CanonicalTomlTable material, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(glb);
        ArgumentNullException.ThrowIfNull(glbPath);
        ArgumentNullException.ThrowIfNull(material);

        problem = UnauthoredImage(material);
        if (problem is not null) return glb;
        if (!GlbBinary.TryRead(glb, out var gltf, out var bin)) return glb;
        if (gltf["materials"] is not JsonArray materials || materialIndex < 0 || materialIndex >= materials.Count) return glb;
        if (materials[materialIndex] is not JsonObject target) return glb;

        var before = gltf.ToJsonString();
        var pbr = target["pbrMetallicRoughness"] as JsonObject ?? [];
        target["pbrMetallicRoughness"] = pbr;

        if (Colour(material, "BaseColorFactor", 4) is { } baseColor) pbr["baseColorFactor"] = Floats(baseColor);
        if (Number(material, "MetallicFactor") is { } metallic) pbr["metallicFactor"] = metallic;
        if (Number(material, "RoughnessFactor") is { } roughness) pbr["roughnessFactor"] = roughness;
        if (Colour(material, "EmissiveFactor", 3) is { } emissive) target["emissiveFactor"] = Floats(emissive);
        if (material.Value("AlphaMode") is string alphaMode) target["alphaMode"] = alphaMode.ToUpperInvariant();
        if (Number(material, "AlphaCutoff") is { } cutoff) target["alphaCutoff"] = cutoff;
        if (material.Value("DoubleSided") is bool doubleSided) target["doubleSided"] = doubleSided;
        if (Number(material, "TransmissionFactor") is { } transmission)
        {
            var extensions = target["extensions"] as JsonObject ?? [];
            target["extensions"] = extensions;
            extensions["KHR_materials_transmission"] = new JsonObject { ["transmissionFactor"] = transmission };
        }

        Bind(gltf, glbPath, pbr, "baseColorTexture", material, "BaseColorTexture", info =>
        {
            var offset = Floats2(material, "BaseColorUvOffset");
            var scale = Floats2(material, "BaseColorUvScale");
            var rotation = Number(material, "BaseColorUvRotation");
            if (offset is null && scale is null && rotation is null) return;
            var extensions = info["extensions"] as JsonObject ?? [];
            info["extensions"] = extensions;
            var transform = new JsonObject();
            if (offset is { } o) transform["offset"] = Floats(o);
            if (scale is { } s) transform["scale"] = Floats(s);
            if (rotation is { } r) transform["rotation"] = r;
            extensions["KHR_texture_transform"] = transform;
            Declare(gltf, "KHR_texture_transform");
        });
        Bind(gltf, glbPath, pbr, "metallicRoughnessTexture", material, "MetallicRoughnessTexture", null);
        Bind(gltf, glbPath, target, "normalTexture", material, "NormalTexture", info =>
        {
            if (Number(material, "NormalScale") is { } scale) info["scale"] = scale;
        });
        Bind(gltf, glbPath, target, "occlusionTexture", material, "OcclusionTexture", info =>
        {
            if (Number(material, "OcclusionStrength") is { } strength) info["strength"] = strength;
        });
        Bind(gltf, glbPath, target, "emissiveTexture", material, "EmissiveTexture", null);

        return gltf.ToJsonString() == before ? glb : GlbBinary.Write(gltf, bin);
    }

    /// <summary>An authored texture is a PNG or JPEG; KTX2 is what the build writes from one, and a GLB in <c>assets/</c> never names it.</summary>
    private static string? UnauthoredImage(CanonicalTomlTable material)
    {
        foreach (var key in s_textureKeys)
        {
            if (material.Value(key) is not CanonicalInlineTable inline || !AssetReferenceCodec.TryRead(inline, out var reference)) continue;
            if (s_authoredImageExtensions.Contains(Path.GetExtension(reference.Path), StringComparer.OrdinalIgnoreCase)) continue;
            return $"{key} names '{reference.Path}', which is not a PNG or JPEG; an authored texture is one of those, and KTX2 is build output";
        }

        return null;
    }

    private static void Bind(JsonObject gltf, string glbPath, JsonObject owner, string infoKey, CanonicalTomlTable material, string documentKey, Action<JsonObject>? decorate)
    {
        if (material.Value(documentKey) is not CanonicalInlineTable inline) return;
        if (!AssetReferenceCodec.TryRead(inline, out var reference))
        {
            owner.Remove(infoKey);
            return;
        }

        var textureIndex = TextureFor(gltf, glbPath, reference);
        var info = owner[infoKey] as JsonObject ?? [];
        owner[infoKey] = info;
        info["index"] = textureIndex;
        decorate?.Invoke(info);
    }

    /// <summary>The texture whose image names <paramref name="reference"/>'s path, added if the GLB has none.</summary>
    private static int TextureFor(JsonObject gltf, string glbPath, AssetReference reference)
    {
        var images = gltf["images"] as JsonArray ?? [];
        gltf["images"] = images;
        var textures = gltf["textures"] as JsonArray ?? [];
        gltf["textures"] = textures;

        var imageIndex = -1;
        for (var i = 0; i < images.Count; i++)
        {
            if (images[i] is JsonObject image && image["uri"]?.GetValue<string>() is { } uri
                && MeshContainer.AssetPathFor(glbPath, uri) == reference.Path)
            {
                imageIndex = i;
                break;
            }
        }

        if (imageIndex < 0)
        {
            var extension = Path.GetExtension(reference.Path).ToLowerInvariant();
            images.Add((JsonNode)new JsonObject
            {
                ["uri"] = MeshContainer.UriFor(glbPath, reference.Path),
                ["mimeType"] = extension is ".jpg" or ".jpeg" ? "image/jpeg" : "image/png",
            });
            imageIndex = images.Count - 1;
        }

        for (var i = 0; i < textures.Count; i++)
        {
            if (textures[i] is JsonObject texture
                && (texture["source"]?.GetValue<int>() == imageIndex
                    || texture["extensions"]?["KHR_texture_basisu"]?["source"]?.GetValue<int>() == imageIndex))
            {
                return i;
            }
        }

        textures.Add((JsonNode)new JsonObject { ["source"] = imageIndex });
        return textures.Count - 1;
    }

    private static void Declare(JsonObject gltf, string extension)
    {
        var used = gltf["extensionsUsed"] as JsonArray ?? [];
        gltf["extensionsUsed"] = used;
        if (!used.Any(node => node?.GetValue<string>() == extension)) used.Add((JsonNode)JsonValue.Create(extension));
    }

    private static double? Number(CanonicalTomlTable table, string key) => table.Value(key) switch
    {
        double d => d,
        long l => l,
        _ => null,
    };

    private static double[]? Colour(CanonicalTomlTable material, string key, int channels)
    {
        if (material.Value(key) is not CanonicalTomlTable colour) return null;
        var names = new[] { "r", "g", "b", "a" };
        var result = new double[channels];
        for (var i = 0; i < channels; i++)
        {
            result[i] = Number(colour, names[i]) ?? (i == 3 ? 1.0 : 0.0);
        }

        return result;
    }

    private static double[]? Floats2(CanonicalTomlTable material, string key)
    {
        if (material.Value(key) is not IReadOnlyList<object> list || list.Count != 2) return null;
        return list.Select(item => item switch { double d => d, long l => (double)l, _ => 0.0 }).ToArray();
    }

    private static JsonArray Floats(IEnumerable<double> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());
}
