using Tomlyn.Model;

using Zio;

namespace Paradise.Assets.Documents;

/// <summary>
/// The sidecar meta document: one <c>&lt;asset&gt;.meta.toml</c> per foreign/binary asset,
/// carrying the asset's GUID and its per-asset import settings.
/// </summary>
/// <remarks>
/// <para>
/// Sidecars exist for assets whose bytes cannot carry an id — GLBs, textures, audio banks. The
/// project's own text documents (<c>*.scene.toml</c>, config) carry their identity in-file and
/// get no sidecar. References between documents stay <b>paths</b>; the GUID is authoring
/// identity, which is what defangs the classic sidecar failure: a lost sidecar degrades to a
/// re-mint plus a re-link by content hash, instead of breaking every reference.
/// </para>
/// <para>
/// Sidecars are minted and moved by tooling only (<c>mv</c> moves the pair and rewrites the
/// referencing documents); <c>verify</c> fails on orphans and duplicate GUIDs. Import settings
/// replace the filename-token heuristics as the override mechanism — the token defaults remain
/// the fallback when a setting is absent.
/// </para>
/// </remarks>
public sealed class SidecarMeta
{
    /// <summary>The only <c>schema_version</c> this build reads or writes.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>The suffix appended to an asset's file name to form its sidecar's.</summary>
    public const string Suffix = ".meta.toml";

    /// <summary>Creates a sidecar document.</summary>
    /// <param name="guid">The asset's authoring identity.</param>
    /// <param name="kind">What the asset is.</param>
    public SidecarMeta(Guid guid, SidecarAssetKind kind)
    {
        Guid = guid;
        Kind = kind;
    }

    /// <summary>The asset's authoring identity.</summary>
    public Guid Guid { get; }

    /// <summary>What the asset is.</summary>
    public SidecarAssetKind Kind { get; }

    /// <summary>
    /// Texture import settings. Only meaningful when <see cref="Kind"/> is
    /// <see cref="SidecarAssetKind.Texture"/>; absent means "use the defaults".
    /// </summary>
    public TextureImportSettings? Texture { get; set; }

    /// <summary>Mints a sidecar for a new asset: fresh GUID, default settings.</summary>
    /// <param name="kind">What the asset is.</param>
    public static SidecarMeta Mint(SidecarAssetKind kind) => new(Guid.NewGuid(), kind);

    /// <summary>The sidecar path for <paramref name="assetPath"/> (its full name plus <see cref="Suffix"/>).</summary>
    public static UPath PathFor(UPath assetPath)
    {
        assetPath.AssertNotNull(nameof(assetPath));
        return new UPath(assetPath.FullName + Suffix);
    }

    /// <summary>Whether <paramref name="path"/> is a sidecar path.</summary>
    public static bool IsSidecarPath(UPath path) => path.FullName.EndsWith(Suffix, StringComparison.Ordinal);

    /// <summary>The asset path a sidecar at <paramref name="sidecarPath"/> describes.</summary>
    /// <exception cref="ArgumentException"><paramref name="sidecarPath"/> is not a sidecar path.</exception>
    public static UPath AssetPathFor(UPath sidecarPath)
    {
        if (!IsSidecarPath(sidecarPath))
        {
            throw new ArgumentException($"'{sidecarPath}' does not end in '{Suffix}'.", nameof(sidecarPath));
        }

        return new UPath(sidecarPath.FullName[..^Suffix.Length]);
    }

    /// <summary>Reads and validates the sidecar at <paramref name="path"/>.</summary>
    /// <exception cref="SidecarMetaException">The file is unreadable, not TOML, or not a valid sidecar.</exception>
    public static SidecarMeta Load(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        path.AssertNotNull(nameof(path));

        string text;
        try
        {
            text = fileSystem.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new SidecarMetaException(path.FullName, $"could not be read ({error.Message})", error);
        }

        return Parse(text, path.FullName);
    }

    /// <summary>Validates an already-read sidecar. The filesystem-free half of <see cref="Load"/>.</summary>
    /// <exception cref="SidecarMetaException">The text is not TOML, or not a valid sidecar.</exception>
    public static SidecarMeta Parse(string toml, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(toml);
        ArgumentNullException.ThrowIfNull(sourceName);

        Exception Fail(string problem) => new SidecarMetaException(sourceName, problem);

        var root = TomlDocumentReader.Parse(toml, Fail);
        TomlDocumentReader.RejectUnknownKeys(root, "at the document root", ["schema_version", "guid", "kind", "texture"], Fail);

        var schemaVersion = TomlDocumentReader.RequireInteger(root, "schema_version", "at the document root", Fail);
        if (schemaVersion != SupportedSchemaVersion)
        {
            throw Fail($"declares schema_version = {schemaVersion}, which this build cannot read (supported: {SupportedSchemaVersion})");
        }

        var guidText = TomlDocumentReader.RequireString(root, "guid", "at the document root", Fail);
        if (!DocumentGuid.TryParse(guidText, out var guid) || guid == Guid.Empty)
        {
            throw Fail($"holds '{guidText}' where 'guid' must be a non-empty UUID");
        }

        var kindText = TomlDocumentReader.RequireString(root, "kind", "at the document root", Fail);
        var kind = kindText switch
        {
            "texture" => SidecarAssetKind.Texture,
            "mesh" => SidecarAssetKind.Mesh,
            "audio" => SidecarAssetKind.Audio,
            _ => throw Fail($"sets kind = \"{kindText}\"; expected \"texture\", \"mesh\" or \"audio\""),
        };

        var meta = new SidecarMeta(guid, kind);
        if (TomlDocumentReader.OptionalTable(root, "texture", "at the document root", Fail) is { } texture)
        {
            if (kind != SidecarAssetKind.Texture)
            {
                throw Fail($"has a [texture] table but kind = \"{kindText}\" — import settings must match the asset's kind");
            }

            meta.Texture = ReadTexture(texture, Fail);
        }

        return meta;
    }

    /// <summary>Renders this sidecar as canonical TOML text.</summary>
    public string Write() => CanonicalTomlWriter.WriteString(ToCanonical());

    /// <summary>Writes this sidecar to <paramref name="path"/> as UTF-8 without BOM.</summary>
    public void Save(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        path.AssertNotNull(nameof(path));
        fileSystem.WriteAllBytes(path, CanonicalTomlWriter.WriteBytes(ToCanonical()));
    }

    private static TextureImportSettings ReadTexture(TomlTable table, Func<string, Exception> fail)
    {
        TomlDocumentReader.RejectUnknownKeys(table, "in [texture]", ["preset"], fail);

        var preset = TomlDocumentReader.OptionalString(table, "preset", "in [texture]", fail) switch
        {
            null => (TexturePreset?)null,
            "color" => TexturePreset.Color,
            "color-linear" => TexturePreset.ColorLinear,
            "normal" => TexturePreset.Normal,
            "data" => TexturePreset.Data,
            { } other => throw fail(
                $"sets preset = \"{other}\" in [texture]; expected \"color\", \"color-linear\", \"normal\" or \"data\""),
        };

        return new TextureImportSettings { Preset = preset };
    }

    private CanonicalTomlTable ToCanonical()
    {
        var root = new CanonicalTomlTable
        {
            { "schema_version", (long)SupportedSchemaVersion },
            { "guid", DocumentGuid.Format(Guid) },
            {
                "kind", Kind switch
                {
                    SidecarAssetKind.Texture => "texture",
                    SidecarAssetKind.Mesh => "mesh",
                    SidecarAssetKind.Audio => "audio",
                    _ => throw new InvalidOperationException($"Unknown kind {Kind}."),
                }
            },
        };

        if (Texture is { Preset: { } preset })
        {
            root.Add("texture", new CanonicalTomlTable
            {
                {
                    "preset", preset switch
                    {
                        TexturePreset.Color => "color",
                        TexturePreset.ColorLinear => "color-linear",
                        TexturePreset.Normal => "normal",
                        TexturePreset.Data => "data",
                        _ => throw new InvalidOperationException($"Unknown preset {preset}."),
                    }
                },
            });
        }

        return root;
    }
}

/// <summary>What a sidecar's asset is. Scenes and config documents carry ids in-file — no sidecar.</summary>
public enum SidecarAssetKind
{
    /// <summary>A source image (<c>textures/**</c>, <c>sprites/**</c>), compiled to KTX2.</summary>
    Texture,

    /// <summary>A source GLB (<c>models/**</c>), re-emitted with KTX2 texture sidecars.</summary>
    Mesh,

    /// <summary>A committed audio bank (<c>audio/**</c>), verified and copied through.</summary>
    Audio,
}

/// <summary>Per-asset texture import settings. Absent values fall back to the token defaults.</summary>
public sealed class TextureImportSettings
{
    /// <summary>The encoding preset override, or <see langword="null"/> for the filename-token default.</summary>
    public TexturePreset? Preset { get; set; }
}

/// <summary>
/// The texture encoding presets, mirroring the pipeline's <c>TextureEncodingPreset</c> family:
/// UASTC always, differing in transfer tagging and normal-map treatment.
/// </summary>
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

/// <summary>A sidecar meta document could not be read, parsed, or validated.</summary>
public sealed class SidecarMetaException : Exception
{
    /// <summary>Creates an exception describing a problem with <paramref name="sourceName"/>.</summary>
    /// <param name="sourceName">The sidecar path, or another name for the source text.</param>
    /// <param name="problem">The problem, phrased to follow the source name.</param>
    /// <param name="innerException">The underlying failure, when there was one.</param>
    public SidecarMetaException(string sourceName, string problem, Exception? innerException = null)
        : base($"Sidecar meta '{sourceName}' {problem}.", innerException)
    {
        SourceName = sourceName;
    }

    /// <summary>The sidecar this failure is about.</summary>
    public string SourceName { get; }
}
