using Paradise.Assets.Documents;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// One import-settings domain: the root table a build step owns in a sidecar, and how to check
/// it.
/// </summary>
/// <remarks>
/// This is the whole contract generic code sees. Verify, and anything else that walks a
/// sidecar's settings without owning them, goes through this interface and the
/// <see cref="ImportSettings"/> registry — only the step that owns a domain touches its concrete
/// type (<see cref="TextureImportSettings.PresetOf"/>), because only that step can give the
/// fields meaning.
/// </remarks>
public interface IImportSettingsDomain
{
    /// <summary>The root table name this domain owns (<c>[texture]</c> → <c>"texture"</c>).</summary>
    string Name { get; }

    /// <summary>The first problem in a table under this domain, or <see langword="null"/>.</summary>
    string? Problem(CanonicalTomlTable settings);
}

/// <summary>
/// The texture encoding presets, mirroring <c>KtxCreate.TextureEncodingPreset</c>:
/// UASTC always, differing in transfer tagging and normal-map treatment.
/// </summary>
/// <remarks>
/// Lives in the PIPELINE, not the document format. A sidecar carries settings domains as opaque
/// tables; what "preset" means — and that it exists at all — is knowledge of the step that
/// encodes textures, and keeping it here means a new setting never grows the format layer.
/// </remarks>
public enum TexturePreset
{
    /// <summary>sRGB-encoded color (albedo). The default for plain images.</summary>
    Color,

    /// <summary>Linear color — masks, ORM-style packed maps.</summary>
    ColorLinear,

    /// <summary>Tangent-space normal map: linear, normal-mode encoding.</summary>
    Normal,

    /// <summary>Non-color data.</summary>
    Data,
}

/// <summary>
/// The texture step's reading of a sidecar's <c>[texture]</c> settings table.
/// </summary>
/// <remarks>
/// Closed, like the transform payload and for the same reason: nothing reads an unknown key
/// here, so an unknown key is a typo — and a typo'd <c>preset</c> silently falling back to the
/// token default is exactly the quiet wrong-output failure settings exist to prevent.
/// </remarks>
public sealed class TextureImportSettings : IImportSettingsDomain
{
    /// <summary>The settings domain the texture step owns.</summary>
    public const string Domain = "texture";

    /// <summary>The encoding preset override; absent means the filename-token default.</summary>
    public const string PresetKey = "preset";

    /// <summary>The one instance, registered in <see cref="ImportSettings.Domains"/>.</summary>
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

    /// <summary>The preset a (valid) settings table declares, or <see langword="null"/> when absent.</summary>
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

/// <summary>
/// Which import-settings domains exist — the registry <c>verify</c> checks sidecars against.
/// </summary>
/// <remarks>
/// This lives beside the build steps because they are the answer: a domain exists exactly when a
/// step reads it, and adding one is adding its instance to <see cref="Domains"/>. The format
/// layer carries settings opaquely and cannot police them without re-importing this knowledge,
/// which is the coupling removing <c>kind</c> got rid of.
/// </remarks>
public static class ImportSettings
{
    /// <summary>Every settings domain a build step reads.</summary>
    public static IReadOnlyList<IImportSettingsDomain> Domains { get; } = [TextureImportSettings.Instance];

    /// <summary>The domain owning <paramref name="name"/>, or <see langword="null"/> when no step reads it.</summary>
    public static IImportSettingsDomain? Find(string name)
    {
        foreach (var domain in Domains)
        {
            if (string.Equals(domain.Name, name, StringComparison.Ordinal)) return domain;
        }

        return null;
    }
}
