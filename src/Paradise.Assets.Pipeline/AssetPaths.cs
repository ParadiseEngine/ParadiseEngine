using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// The files under <c>assets/</c> as one ordinal set, taken once per run.
/// </summary>
/// <remarks>
/// A reference is checked against this rather than against <c>FileExists</c> because the OS
/// below may be case-insensitive (macOS, Windows) or normalisation-insensitive (APFS), so
/// <c>../Textures/Rust.png</c> passes there, the KTX2 is written at the real case, and the
/// shipped mesh points at a file Linux cannot find (issue #202). The set holds exactly the names
/// the directory walk returned, so a reference resolves only when it is spelled as the file is.
/// </remarks>
public sealed class AssetPaths
{
    private readonly HashSet<UPath> _files;
    private readonly Dictionary<string, UPath> _byFoldedName;

    private AssetPaths(UPath root, List<UPath> files)
    {
        Root = root;
        Files = files;
        _files = [.. files];
        _byFoldedName = new Dictionary<string, UPath>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) _byFoldedName.TryAdd(file.FullName, file);
    }

    public UPath Root { get; }

    /// <summary>Every file, sidecars and junk included, in ordinal order.</summary>
    public IReadOnlyList<UPath> Files { get; }

    public static AssetPaths Scan(IFileSystem fileSystem, UPath assetsRoot)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        assetsRoot.AssertAbsolute(nameof(assetsRoot));

        var files = fileSystem.DirectoryExists(assetsRoot)
            ? fileSystem.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories)
                .OrderBy(p => p.FullName, StringComparer.Ordinal)
                .ToList()
            : [];
        return new AssetPaths(assetsRoot, files);
    }

    public bool IsUnderRoot(UPath path) => path.IsInDirectory(Root, recursive: true);

    /// <summary>Case- and normalisation-exact.</summary>
    public bool Contains(UPath path) => _files.Contains(path);

    /// <summary>The real spelling of a path that differs only by case, for an error message that says what to fix.</summary>
    public bool TryFindIgnoringCase(UPath path, out UPath actual)
        => _byFoldedName.TryGetValue(path.FullName, out actual) && actual != path;

    /// <summary>What went wrong with a reference, or null when it resolves; the message continues "<c>{source}: references '{reference}', which …</c>".</summary>
    public string? Problem(UPath resolved, string reference)
    {
        if (!IsUnderRoot(resolved))
        {
            return $"references '{reference}', which resolves outside assets/ ('{resolved}'); a build cannot ship what it does not own";
        }

        if (Contains(resolved)) return null;

        if (TryFindIgnoringCase(resolved, out var actual))
        {
            return $"references '{reference}', which does not exist under assets/ — '{Relative(actual)}' does, and " +
                "references are case-exact because a build that passes on this machine ships a path Linux cannot open";
        }

        return $"references '{reference}', which does not exist under assets/ (a moved or renamed file; the reference moves with it)";
    }

    public string Relative(UPath path) => path.FullName[(Root.FullName.Length + 1)..];
}
