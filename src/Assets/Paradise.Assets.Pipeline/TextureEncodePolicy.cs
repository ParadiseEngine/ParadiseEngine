using System.Text.Json.Nodes;

using Paradise.Assets.Project;

namespace Paradise.Assets.Pipeline;

/// <summary>Selects UASTC texture encoding with Zstd supercompression.</summary>
/// <remarks>UASTC preserves detailed and saturated source colors when transcoded to BC7; ETC1S visibly degrades them.</remarks>
public enum TextureEncodingPreset
{
    /// <summary>Base colour, emissive.</summary>
    UastcColorSrgb,

    /// <summary>Linear colour: masks, height, displacement.</summary>
    UastcColorLinear,

    /// <summary>Packed data: metallic, roughness, occlusion.</summary>
    UastcDataLinear,

    UastcNormalLinear,
}

/// <summary>Selects texture presets from material slots or image names and builds <c>ktx create</c> arguments.</summary>
/// <remarks>
/// Pure functions over strings and glTF JSON, without tool or filesystem access.
/// The linear container tag avoids Godot's double sRGB decode, but causes <c>ktx</c> to filter
/// colour mipmaps in gamma space, making them slightly darker at distance.
/// </remarks>
public static class TextureEncodePolicy
{
    /// <summary>Builds KTX v5 arguments for UASTC encoding with Zstandard supercompression.</summary>
    /// <remarks>
    /// KTX2 output and top-left origin are implicit. The format and input transfer function
    /// are explicit, and positional paths are input then output despite this API's parameter order.
    /// <see cref="TextureQuality.Fast"/> uses UASTC quality 0 for iteration rather than shipping;
    /// full quality uses 2.
    /// </remarks>
    public static string CreateArguments(TextureEncodingPreset preset, string outputPath, string sourcePath, TextureQuality quality)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(sourcePath);

        var arguments = new List<string> { "create", "--generate-mipmap", "--format", "R8G8B8A8_UNORM", "--assign-tf", "linear" };

        // Colour texels remain sRGB-encoded. Godot 4.7 decodes KHR_texture_basisu sRGB data
        // both at import and through the sRGB VRAM format; a linear DFD prevents the first decode.
        // The .NET runtime chooses its GPU format by usage (ColorSrgb → BC7-sRGB), not the DFD.
        // Revisit the linear tag when Godot fixes the double decode.
        if (preset == TextureEncodingPreset.UastcNormalLinear) arguments.Add("--normal-mode");

        arguments.AddRange(["--encode", "uastc", "--uastc-quality", quality == TextureQuality.Fast ? "0" : "2", "--zstd", "10"]);
        arguments.Add(ProcessTools.QuoteArgument(sourcePath));
        arguments.Add(ProcessTools.QuoteArgument(outputPath));
        return string.Join(" ", arguments);
    }

    /// <summary>The preset each image gets from the material slots that bind it; an image bound to nothing is absent, and the caller falls back to its name.</summary>
    public static Dictionary<int, TextureEncodingPreset> MaterialPresets(JsonObject gltf)
    {
        ArgumentNullException.ThrowIfNull(gltf);

        var presets = new Dictionary<int, TextureEncodingPreset>();
        if (gltf["materials"] is not JsonArray materials
            || gltf["textures"] is not JsonArray textures
            || gltf["images"] is not JsonArray images)
        {
            return presets;
        }

        foreach (var material in materials.OfType<JsonObject>())
        {
            var pbr = material["pbrMetallicRoughness"] as JsonObject;
            Bind(pbr?["baseColorTexture"], TextureEncodingPreset.UastcColorSrgb);
            Bind(material["emissiveTexture"], TextureEncodingPreset.UastcColorSrgb);
            Bind(pbr?["metallicRoughnessTexture"], TextureEncodingPreset.UastcDataLinear);
            Bind(material["normalTexture"], TextureEncodingPreset.UastcNormalLinear);
            Bind(material["occlusionTexture"], TextureEncodingPreset.UastcDataLinear);
        }

        return presets;

        void Bind(JsonNode? textureInfo, TextureEncodingPreset preset)
        {
            var textureIndex = textureInfo?["index"]?.GetValue<int>();
            if (textureIndex is not { } t || t < 0 || t >= textures.Count || textures[t] is not JsonObject texture) return;

            var imageIndex = texture["source"]?.GetValue<int>();
            if (imageIndex is not { } i || i < 0 || i >= images.Count) return;

            presets[i] = presets.TryGetValue(i, out var existing) ? Merge(existing, preset) : preset;
        }
    }

    /// <summary>One image bound to two slots keeps the more specific preset: normal over data over linear colour over sRGB.</summary>
    internal static TextureEncodingPreset Merge(TextureEncodingPreset existing, TextureEncodingPreset next)
    {
        if (existing == TextureEncodingPreset.UastcNormalLinear || next == TextureEncodingPreset.UastcNormalLinear) return TextureEncodingPreset.UastcNormalLinear;
        if (existing == TextureEncodingPreset.UastcDataLinear || next == TextureEncodingPreset.UastcDataLinear) return TextureEncodingPreset.UastcDataLinear;
        if (existing == TextureEncodingPreset.UastcColorLinear || next == TextureEncodingPreset.UastcColorLinear) return TextureEncodingPreset.UastcColorLinear;
        return TextureEncodingPreset.UastcColorSrgb;
    }

    public static TextureEncodingPreset PresetFromImageName(JsonObject image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return PresetFromImageName(image["name"]?.GetValue<string>() ?? "");
    }

    /// <summary>
    /// Matches whole name tokens only: a substring rule made <c>Damask</c> a mask and
    /// <c>Chaos_Albedo</c> an AO map, encoding colour as linear data with no error.
    /// </summary>
    public static TextureEncodingPreset PresetFromImageName(string imageName)
    {
        ArgumentNullException.ThrowIfNull(imageName);

        var tokens = new HashSet<string>(NameTokens(imageName), StringComparer.OrdinalIgnoreCase);
        if (tokens.Overlaps(["Normal", "NormalMap", "Bump"])) return TextureEncodingPreset.UastcNormalLinear;
        if (tokens.Overlaps(["Metallic", "Metalness", "Roughness", "Gloss", "Occlusion", "AO"])) return TextureEncodingPreset.UastcDataLinear;
        if (tokens.Overlaps(["Mask", "Height", "Displacement"])) return TextureEncodingPreset.UastcColorLinear;
        return TextureEncodingPreset.UastcColorSrgb;
    }

    /// <summary>Splits on separators and on lower→upper case boundaries: <c>Wall_NormalMap</c> → Wall, NormalMap, Normal, Map.</summary>
    internal static IEnumerable<string> NameTokens(string imageName)
    {
        foreach (var segment in imageName.Split(['_', '-', ' ', '.', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            yield return segment;

            var start = 0;
            for (var i = 1; i < segment.Length; i++)
            {
                var boundary = char.IsUpper(segment[i]) && !char.IsUpper(segment[i - 1])
                    || char.IsDigit(segment[i]) != char.IsDigit(segment[i - 1]);
                if (!boundary) continue;

                if (i > start) yield return segment.Substring(start, i - start);
                start = i;
            }

            if (start > 0 && start < segment.Length) yield return segment.Substring(start);
        }
    }
}
