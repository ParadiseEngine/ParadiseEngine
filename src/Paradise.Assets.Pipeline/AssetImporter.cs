using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>Everything an importer may draw on; <paramref name="Meta"/> is null only when the asset is itself a sidecar.</summary>
public sealed record ImportContext(
    IFileSystem FileSystem,
    UPath AssetsRoot,
    UPath Asset,
    string Source,
    SidecarMeta? Meta,
    BuildProfile Profile,
    ProjectOutputTarget Target,
    IFileSystem Output,
    ArtifactCache Cache,
    ITextureEncoder? Encoder,
    Action<string>? Log)
{
    /// <summary>Case-insensitive, with dot.</summary>
    public bool HasExtension(params ReadOnlySpan<string> extensions)
    {
        var extension = Asset.GetExtensionWithDot() ?? string.Empty;
        foreach (var claimed in extensions)
        {
            if (string.Equals(claimed, extension, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public bool IsManifest => Source == AssetProjectLayout.ManifestFileName;
}

/// <summary>One link in the import chain: handle the asset or decline and let the next link try.</summary>
/// <remarks>
/// Importers claim inside <see cref="Import"/> rather than declaring extensions, so a project can
/// append one that shadows a built-in (library-only today — issue #208). Decline first, validate
/// next, write last: the chain shares one output mount, so an early write lands in the manifest
/// under whoever ends up handling the asset, or survives in a tree the failed build already
/// declared suspect.
/// </remarks>
public interface IAssetImporter
{
    string Name { get; }

    /// <summary>Whether output is a pure function of source and sidecar bytes; anything keyed on tool versions, profile or referenced files must answer false and cache on its complete input.</summary>
    bool DeterministicCopy { get; }

    /// <summary>False for outputs addressed by path alone (a config).</summary>
    bool RecordsIdentity { get; }

    /// <summary>A failure is reported through <paramref name="errors"/>, prefixed with the source, and writes nothing.</summary>
    bool Import(ImportContext context, List<string> errors);
}

public static class AssetImporters
{
    /// <summary>Lowest precedence first: the chain is walked backwards so an appended importer shadows the built-in it replaces.</summary>
    public static IReadOnlyList<IAssetImporter> All { get; } =
    [
        new ConfigImporter(),
        new PrefabImporter(),
        new AudioImporter(),
        new MeshImporter(),
        new TextureImporter(),
    ];
}
