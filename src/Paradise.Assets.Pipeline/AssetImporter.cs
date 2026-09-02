using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>Everything an importer may draw on.</summary>
/// <remarks>
/// <paramref name="FileSystem"/> is the source tree as the build index sees it: every file read
/// or asked about through it is recorded, and the asset is rebuilt when any of them changes. It
/// is read-only, case-exact under <c>assets/</c>, and refuses directory listings. Anything the
/// output depends on that is NOT read through it — a tool's version, the profile's settings — is
/// the runner's to fold into the index environment; the built-ins have nothing else.
/// <paramref name="Meta"/> is the asset's sidecar, which verify guarantees exists before a build
/// runs.
/// </remarks>
public sealed record ImportContext(
    IFileSystem FileSystem,
    AssetPaths Sources,
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
    public UPath AssetsRoot => Sources.Root;

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

    /// <summary>Resolves a reference the asset makes, checks it against the real tree, and records the dependency; returns the error to report, or null when it resolves.</summary>
    public string? CheckReference(string reference, out UPath resolved)
    {
        ArgumentNullException.ThrowIfNull(reference);

        resolved = (Asset.GetDirectory() / Uri.UnescapeDataString(reference)).ToAbsolute();
        var problem = Sources.Problem(resolved, reference);
        // Through the observed filesystem, so the index rebuilds this asset when the file it
        // references appears or disappears.
        FileSystem.FileExists(resolved);
        return problem is null ? null : $"{Source}: {problem}";
    }
}

/// <summary>One link in the import chain: handle the asset or decline and let the next link try.</summary>
/// <remarks>
/// Importers claim inside <see cref="Import"/> rather than declaring extensions, so a project can
/// append one that shadows a built-in (library-only today — issue #208). Decline first, validate
/// next, write last: the chain shares one output mount, so an early write lands in the manifest
/// under whoever ends up handling the asset, or survives in a tree the failed build already
/// declared suspect. Read every input through <see cref="ImportContext.FileSystem"/> and nothing
/// else; the build index reuses the output whenever everything read there is unchanged.
/// </remarks>
public interface IAssetImporter
{
    string Name { get; }

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
