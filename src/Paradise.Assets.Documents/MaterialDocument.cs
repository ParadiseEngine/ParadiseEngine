using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Documents;

/// <summary>Reads authored <c>*.material</c> TOML with five texture slots encoded as asset references.</summary>
/// <remarks>
/// The reference-aware importer lets build, verify, move and remove follow texture identities.
/// Built materials retain their suffix and use the profile's TOML or JSON format;
/// <c>ExportDocumentReader.ReadMaterial</c> detects the format from the contents.
/// </remarks>
public static class MaterialDocument
{
    public const string Suffix = ".material";

    /// <summary>The texture slots, by their contract field names. Absent or <c>{}</c> means none.</summary>
    public static readonly IReadOnlyList<string> TextureKeys =
    [
        "BaseColorTexture",
        "MetallicRoughnessTexture",
        "NormalTexture",
        "OcclusionTexture",
        "EmissiveTexture",
    ];

    public static bool IsMaterialPath(UPath path)
        => string.Equals(path.GetExtensionWithDot(), Suffix, StringComparison.OrdinalIgnoreCase);

    /// <exception cref="FormatException">Not a readable material document; the message names the problem.</exception>
    public static CanonicalTomlTable Parse(string toml, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(toml);
        ArgumentNullException.ThrowIfNull(sourceName);

        Exception Fail(string problem) => new FormatException($"{sourceName}: {problem}");
        var table = TomlDocumentReader.Parse(toml, Fail);
        var model = TomlDocumentReader.ToCanonical(table, "in the document", Fail);

        foreach (var key in TextureKeys)
        {
            if (model.Value(key) is { } value && value is not CanonicalInlineTable)
            {
                throw Fail($"'{key}' must be an asset reference {{ guid, path }} (or {{}} for none), not {Describe(value)}");
            }
        }

        return model;
    }

    public static CanonicalTomlTable Load(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return Parse(fileSystem.ReadAllText(path), path.FullName);
    }

    /// <summary>Every texture slot that names an asset, with the field it sits at.</summary>
    public static IEnumerable<(string Key, AssetReference Reference)> References(CanonicalTomlTable material)
    {
        ArgumentNullException.ThrowIfNull(material);

        foreach (var key in TextureKeys)
        {
            if (material.Value(key) is CanonicalInlineTable inline && AssetReferenceCodec.TryRead(inline, out var reference))
            {
                yield return (key, reference);
            }
        }
    }

    /// <summary>The document with <paramref name="follow"/> applied to every texture slot; null when nothing changed, so a caller can skip the write.</summary>
    public static CanonicalTomlTable? Rewrite(CanonicalTomlTable material, Func<AssetReference, AssetReference> follow)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(follow);

        var changed = false;
        var updated = new CanonicalTomlTable();
        foreach (var (key, value) in material)
        {
            if (TextureKeys.Contains(key) && value is CanonicalInlineTable inline && AssetReferenceCodec.TryRead(inline, out var reference))
            {
                var followed = follow(reference);
                if (followed != reference) changed = true;
                updated.Add(key, AssetReferenceCodec.Write(followed));
                continue;
            }

            updated.Add(key, value);
        }

        return changed ? updated : null;
    }

    /// <summary>The document as the runtime reads it: each texture slot replaced by the built path <paramref name="builtPath"/> answers (null drops the slot), every other field verbatim.</summary>
    public static CanonicalTomlTable Bake(CanonicalTomlTable material, Func<AssetReference, string?> builtPath)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(builtPath);

        var baked = new CanonicalTomlTable();
        foreach (var (key, value) in material)
        {
            if (!TextureKeys.Contains(key))
            {
                baked.Add(key, value);
                continue;
            }

            if (value is CanonicalInlineTable inline && AssetReferenceCodec.TryRead(inline, out var reference) && builtPath(reference) is { } path)
            {
                baked.Add(key, path);
            }
        }

        return baked;
    }

    private static string Describe(object value) => value switch
    {
        string => "a string",
        bool => "a boolean",
        long or double => "a number",
        CanonicalTomlTable => "a table",
        _ => value.GetType().Name,
    };
}
