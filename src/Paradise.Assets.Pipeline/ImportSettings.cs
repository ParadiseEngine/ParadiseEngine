using Paradise.Assets.Documents;

namespace Paradise.Assets.Pipeline;

/// <summary>One import-settings domain: the sidecar root table a build step owns, and how to check it; only the owning step reads the concrete type, because only it can give the fields meaning.</summary>
public interface IImportSettingsDomain
{
    string Name { get; }

    /// <summary>The first problem in a table under this domain, or <see langword="null"/>.</summary>
    string? Problem(CanonicalTomlTable settings);
}

/// <summary>Mirrors <see cref="TextureEncodingPreset"/>; lives in the pipeline, not the format, so a new setting never grows the format layer.</summary>
public enum TexturePreset
{
    /// <summary>sRGB colour; the default for plain images.</summary>
    Color,

    /// <summary>Linear colour: masks, ORM-style packed maps.</summary>
    ColorLinear,

    Normal,

    Data,
}

/// <summary>The <c>[texture]</c> domain. Closed: a typo'd <c>preset</c> silently falling back to the token default is the wrong-output failure settings exist to prevent.</summary>
public sealed class TextureImportSettings : IImportSettingsDomain
{
    public const string Domain = "texture";

    /// <summary>Absent means the filename-token default.</summary>
    public const string PresetKey = "preset";

    public static TextureImportSettings Instance { get; } = new();

    private TextureImportSettings()
    {
    }

    /// <inheritdoc />
    public string Name => Domain;

    /// <inheritdoc />
    public string? Problem(CanonicalTomlTable settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var (key, value) in settings)
        {
            if (key != PresetKey)
            {
                return $"holds '{key}' in [{Domain}], which is not a texture setting";
            }

            if (ParsePreset(value) is null)
            {
                return $"sets {PresetKey} = \"{value}\" in [{Domain}]; expected \"color\", \"color-linear\", \"normal\" or \"data\"";
            }
        }

        return null;
    }

    public TexturePreset? PresetOf(CanonicalTomlTable? settings)
        => settings?.Value(PresetKey) is { } value ? ParsePreset(value) : null;

    private static TexturePreset? ParsePreset(object value) => value switch
    {
        "color" => TexturePreset.Color,
        "color-linear" => TexturePreset.ColorLinear,
        "normal" => TexturePreset.Normal,
        "data" => TexturePreset.Data,
        _ => null,
    };
}

/// <summary>The registry <c>verify</c> checks sidecars against; a domain exists exactly when a step reads it, which is why this lives beside the steps and not in the format layer.</summary>
public static class ImportSettings
{
    public static IReadOnlyList<IImportSettingsDomain> Domains { get; } = [TextureImportSettings.Instance, MeshImportSettings.Instance];

    public static IImportSettingsDomain? Find(string name)
    {
        foreach (var domain in Domains)
        {
            if (string.Equals(domain.Name, name, StringComparison.Ordinal)) return domain;
        }

        return null;
    }
}
