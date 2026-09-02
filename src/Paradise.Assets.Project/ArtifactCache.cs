using Zio;

namespace Paradise.Assets.Project;

/// <summary>
/// A directory of derived artifacts addressed by the digest of their inputs.
/// </summary>
/// <remarks>
/// The digest is <see cref="ArtifactDigest"/>, the Blender addon's scheme, so the two caches can
/// one day be one directory (they are not yet: <c>.editor/cache</c> here, <c>.paradise-cache</c>
/// there — issue #204). On ShiningPie that cache took an unchanged re-export from 44 s to 3.6 s.
/// Every failure is non-fatal because the cache is derived data: a miss is slow, never wrong.
/// Stores are atomic (temp sibling, then rename) so a kill or a race cannot leave a wrong-bytes
/// entry; fetches are not, so the output tree can hold a truncated file after a kill (#204).
/// </remarks>
public sealed class ArtifactCache
{
    /// <summary>Overrides the cache location, or disables caching when set to a falsey word.</summary>
    /// <remarks>The name is the addon's, so one variable governs both tools.</remarks>
    public const string LocationEnvironmentVariable = "PARADISE_EXPORT_CACHE";

    private static readonly HashSet<string> s_disabledValues =
        new(["0", "off", "false", "no", "none"], StringComparer.OrdinalIgnoreCase);

    private readonly IFileSystem? _fileSystem;
    private readonly Action<string>? _warn;
    private readonly Lock _gate = new();
    private volatile bool _enabled;
    private volatile bool _prepared;

    /// <summary>Creates a cache rooted at <paramref name="root"/>.</summary>
    public ArtifactCache(IFileSystem fileSystem, UPath root, Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        root.AssertNotNull(nameof(root));

        _fileSystem = fileSystem;
        _warn = warn;
        _enabled = true;
        Root = root.ToAbsolute();
    }

    private ArtifactCache()
    {
        _enabled = false;
    }

    /// <summary>A cache that stores nothing and misses everything.</summary>
    public static ArtifactCache Disabled { get; } = new();

    /// <summary>Whether this cache does anything. False after the root turns out to be unusable.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>The cache root. Meaningless when <see cref="IsEnabled"/> is false.</summary>
    public UPath Root { get; }

    /// <summary>The cache for a project: the environment override, else <see cref="AssetProjectLayout.EditorCache"/>.</summary>
    /// <remarks>Project-local rather than user-global, so <c>clean</c> clears it with everything else derived.</remarks>
    public static ArtifactCache ForProject(IFileSystem fileSystem, AssetProjectLayout layout, Action<string>? warn = null)
        => ForProject(fileSystem, layout, Environment.GetEnvironmentVariable(LocationEnvironmentVariable), warn);

    /// <summary>As <see cref="ForProject(IFileSystem, AssetProjectLayout, Action{string})"/>, with the environment value passed in so tests need not mutate process state.</summary>
    public static ArtifactCache ForProject(
        IFileSystem fileSystem,
        AssetProjectLayout layout,
        string? configuredLocation,
        Action<string>? warn)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);

        var configured = configuredLocation?.Trim() ?? string.Empty;
        if (s_disabledValues.Contains(configured)) return Disabled;
        if (configured.Length == 0) return new ArtifactCache(fileSystem, layout.EditorCache, warn);

        return new ArtifactCache(fileSystem, fileSystem.ConvertPathFromInternal(ExpandUser(configured)), warn);
    }

    /// <summary>Copies the cached entry to <paramref name="destination"/>, whose extension selects it; a copy failure is a miss.</summary>
    public bool TryFetch(string kind, string key, IFileSystem destinationFileSystem, UPath destination)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(destinationFileSystem);
        destination.AssertNotNull(nameof(destination));

        if (!TryGetEntryPath(kind, key, destination, out var entry)) return false;
        if (!_fileSystem!.FileExists(entry)) return false;

        try
        {
            CreateParentDirectory(destinationFileSystem, destination);
            _fileSystem.CopyFileCross(entry, destinationFileSystem, destination, overwrite: true);
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            Warn($"could not reuse '{entry}' ({error.Message}); regenerating");
            return false;
        }

        return true;
    }

    /// <summary>Copies <paramref name="source"/> into the cache; failures are non-fatal.</summary>
    /// <remarks>
    /// The entry's extension comes from <paramref name="source"/> so the cache is browsable, which
    /// means a kind must use one extension consistently: storing <c>.ktx2</c> and fetching with a
    /// <c>.png</c> destination misses silently forever. Fold a varying extension into the kind.
    /// </remarks>
    public void Store(string kind, string key, IFileSystem sourceFileSystem, UPath source)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(sourceFileSystem);
        source.AssertNotNull(nameof(source));

        if (!TryGetEntryPath(kind, key, source, out var entry)) return;
        if (!sourceFileSystem.FileExists(source)) return;

        var directory = entry.GetDirectory();
        var temporary = directory / $"{key}.{Guid.NewGuid():N}.partial";
        try
        {
            if (!_fileSystem!.DirectoryExists(directory)) _fileSystem.CreateDirectory(directory);
            sourceFileSystem.CopyFileCross(source, _fileSystem, temporary, overwrite: false);

            try
            {
                _fileSystem.MoveFile(temporary, entry);
            }
            catch (IOException)
            {
                // Decided in the catch body, NOT a `when` filter: filters run before the throw
                // site's finally blocks, i.e. while MoveFile still holds the filesystem's
                // internal lock — on MemoryFileSystem, FileExists there deadlocks the thread.
                if (!_fileSystem.FileExists(entry)) throw;

                // Another writer landed the same key first. Under content addressing its entry
                // holds these very bytes, so keeping it equals overwriting and avoids a window
                // where the entry is absent — which is why Zio's lack of an atomic
                // replace-if-exists (the Python side's os.replace) does not matter here.
            }
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            Warn($"could not store '{source.GetName()}' ({error.Message})");
        }
        finally
        {
            TryDeleteTemporary(temporary);
        }
    }

    private bool TryGetEntryPath(string kind, string key, UPath like, out UPath entry)
    {
        entry = default;
        if (!_enabled) return false;
        Prepare();
        if (!_enabled) return false;

        entry = Root / kind / (key + like.GetExtensionWithDot());
        return true;
    }

    private void Prepare()
    {
        if (_prepared) return;

        lock (_gate)
        {
            if (_prepared) return;

            try
            {
                if (!_fileSystem!.DirectoryExists(Root)) _fileSystem.CreateDirectory(Root);

                var marker = Root / ".gitignore";
                // Written as bytes so the contents are exactly "*\n" on every platform: no BOM,
                // and no CRLF that would make the file differ from the addon's.
                if (!_fileSystem.FileExists(marker)) _fileSystem.WriteAllBytes(marker, "*\n"u8.ToArray());
            }
            catch (Exception error) when (IsRecoverable(error))
            {
                _enabled = false;
                Warn($"'{Root}' is unusable ({error.Message}); caching is off");
            }

            _prepared = true;
        }
    }

    private void TryDeleteTemporary(UPath temporary)
    {
        try
        {
            if (_fileSystem!.FileExists(temporary)) _fileSystem.DeleteFile(temporary);
        }
        catch (Exception error) when (IsRecoverable(error))
        {
        }
    }

    private static void CreateParentDirectory(IFileSystem fileSystem, UPath path)
    {
        var directory = path.GetDirectory();
        if (!directory.IsNull && !directory.IsEmpty && !fileSystem.DirectoryExists(directory))
        {
            fileSystem.CreateDirectory(directory);
        }
    }

    private static string ExpandUser(string path)
    {
        if (path.Length == 0 || path[0] != '~') return path;
        if (path.Length > 1 && path[1] is not ('/' or '\\')) return path;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length == 0 ? path : home + path[1..];
    }

    private static bool IsRecoverable(Exception error) => error is IOException or UnauthorizedAccessException;

    private void Warn(string message) => _warn?.Invoke($"Artifact cache: {message}.");
}
