using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Documents;

/// <summary>
/// The authored <c>*.material</c> document: the contract's material fields as TOML, with the
/// texture slots as <c>{ guid, path }</c> references rather than the baked paths the runtime
/// reads. Schema-free like a config document — the contract owns the field list — except for
/// the five texture keys, which this reads as references so the graph, <c>mv</c>, <c>rm</c> and
/// <c>verify</c> can follow them, and the build can rebase them to the KTX2 it wrote.
/// </summary>
/// <remarks>
/// Its own kind, not a <c>.toml</c>, because a material references other assets and a config
/// does not: the importer that claims it declares those references. Built, it KEEPS the
/// <c>.material</c> name — a built prefab's slot list then says what kind of document it names,
/// as <c>.mesh</c> and <c>.anim</c> do — and carries TOML or JSON by build profile. A host reads
/// it with <c>ExportDocumentReader.ReadMaterial</c>, which tells the two apart by the first
/// character, never by extension.
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
