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
/// An importer DECLARES the extensions it answers for, compared ignoring case, and is offered
/// only those files; that is what lets <c>verify</c> say which files nothing handles, and what
/// keeps the classifier and the importers agreeing on <c>Foo.PREFAB</c> (issue #208). Inside
/// <see cref="Import"/> it may still decline (the manifest is a <c>.toml</c> no config importer
/// compiles). The chain is a plain list a game's own host passes to <c>BuildHost.Run</c>, an
/// appended importer shadowing the built-in it replaces. Decline first, validate next, write
/// last: the chain shares one output mount, so an early write lands in the manifest under
/// whoever ends up handling the asset, or survives in a tree the failed build already declared
/// suspect. Read every input through <see cref="ImportContext.FileSystem"/> and nothing else;
/// the build index reuses the output whenever everything read there is unchanged.
/// </remarks>
public interface IAssetImporter
{
    string Name { get; }

    /// <summary>With the dot; matched ignoring case.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>False for outputs addressed by path alone (a config).</summary>
    bool RecordsIdentity { get; }

    /// <summary>Offered only files whose extension is in <see cref="Extensions"/>. A failure is reported through <paramref name="errors"/>, prefixed with the source, and writes nothing.</summary>
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

    public static bool Declares(this IAssetImporter importer, UPath path)
    {
        ArgumentNullException.ThrowIfNull(importer);

        var extension = path.GetExtensionWithDot() ?? string.Empty;
        foreach (var declared in importer.Extensions)
        {
            if (string.Equals(declared, extension, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>The importers that would be offered <paramref name="path"/>, highest precedence first; empty means nothing in the chain handles that kind of file.</summary>
    public static IEnumerable<IAssetImporter> Candidates(this IReadOnlyList<IAssetImporter> chain, UPath path)
    {
        ArgumentNullException.ThrowIfNull(chain);

        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].Declares(path)) yield return chain[i];
        }
    }
}
