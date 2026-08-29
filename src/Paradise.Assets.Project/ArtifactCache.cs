using Zio;

namespace Paradise.Assets.Project;

/// <summary>
/// A directory of derived artifacts addressed by the digest of their inputs — the C# half of the
/// cache the Blender addon already writes.
/// </summary>
/// <remarks>
/// <para>
/// Entries live at <c>&lt;root&gt;/&lt;kind&gt;/&lt;digest&gt;&lt;ext&gt;</c> and the digest is
/// <see cref="ArtifactDigest"/>, so a build step that already ran under Blender is a file copy
/// here and vice versa. On ShiningPie this is what took an unchanged re-export from 44 s to
/// 3.6 s; the same arithmetic is what will keep the playmode loop tight.
/// </para>
/// <para>
/// The cache is <b>disposable by construction</b>: deleting the directory only costs time, and
/// every failure inside it is non-fatal — a fetch that cannot copy is reported as a miss and the
/// caller does the real work. The one outcome this type must never produce is a destination that
/// exists but holds the wrong bytes, which is why stores land on a temporary sibling and are
/// renamed into place: an entry is complete or absent, never half-written, even if the process
/// is killed mid-copy or two builds race.
/// </para>
/// <para>
/// A disabled cache (<see cref="Disabled"/>, or the environment variable set to a falsey word)
/// no-ops every method, so callers need no <c>if (cache is not null)</c> branches.
/// </para>
/// </remarks>
public sealed class ArtifactCache
{
    /// <summary>
    /// Overrides the cache location, or disables caching entirely when set to <c>0</c>,
    /// <c>off</c>, <c>false</c>, <c>no</c> or <c>none</c> (case-insensitively).
    /// </summary>
    /// <remarks>
    /// Exists for CI and for bisecting a suspected stale artifact without editing a project. The
    /// name is inherited from the addon so one variable governs both tools.
    /// </remarks>
    public const string LocationEnvironmentVariable = "PARADISE_EXPORT_CACHE";

    private static readonly HashSet<string> s_disabledValues =
        new(["0", "off", "false", "no", "none"], StringComparer.OrdinalIgnoreCase);

    private readonly IFileSystem? _fileSystem;
    private readonly Action<string>? _warn;
    private readonly Lock _gate = new();
    private volatile bool _enabled;
    private volatile bool _prepared;

    /// <summary>
    /// Creates a cache rooted at <paramref name="root"/> on <paramref name="fileSystem"/>.
    /// </summary>
    /// <param name="fileSystem">Where the cache lives. Physical in production, in-memory in tests.</param>
    /// <param name="root">The cache root, typically <see cref="AssetProjectLayout.EditorCache"/>.</param>
    /// <param name="warn">
    /// Receives the non-fatal failures — an unusable root, an entry that could not be copied.
    /// Optional, because a cache that cannot report is still correct; but a tool that swallows
    /// these will look mysteriously slow rather than broken, so callers should pass one.
    /// </param>
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

    /// <summary>
    /// The cache for a project: <see cref="LocationEnvironmentVariable"/> if set, otherwise
    /// <see cref="AssetProjectLayout.EditorCache"/>.
    /// </summary>
    /// <remarks>
    /// Project-local rather than user-global. The cache mirrors one project's artifacts, so it
    /// belongs with the checkout that produced it — and is then deleted by the same command that
    /// clears any other build output.
    /// </remarks>
    /// <param name="fileSystem">The filesystem holding the project.</param>
    /// <param name="layout">The located project.</param>
    /// <param name="warn">See <see cref="ArtifactCache(IFileSystem, UPath, Action{string})"/>.</param>
    public static ArtifactCache ForProject(IFileSystem fileSystem, AssetProjectLayout layout, Action<string>? warn = null)
        => ForProject(fileSystem, layout, Environment.GetEnvironmentVariable(LocationEnvironmentVariable), warn);

    /// <summary>
    /// <see cref="ForProject(IFileSystem, AssetProjectLayout, Action{string})"/> with the
    /// environment variable's value supplied explicitly, so tests need not mutate process state.
    /// </summary>
    /// <param name="fileSystem">The filesystem holding the project.</param>
    /// <param name="layout">The located project.</param>
    /// <param name="configuredLocation">The <see cref="LocationEnvironmentVariable"/> value, or <see langword="null"/> when unset.</param>
    /// <param name="warn">See <see cref="ArtifactCache(IFileSystem, UPath, Action{string})"/>.</param>
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

    /// <summary>
    /// Copies the entry for (<paramref name="kind"/>, <paramref name="key"/>) to
    /// <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// A copy failure is reported and treated as a miss: the caller then does the real work,
    /// which is slow but correct.
    /// </remarks>
    /// <param name="kind">The artifact family, e.g. <c>ktx2</c>. See <see cref="Store"/> for the extension rule.</param>
    /// <param name="key">The digest from <see cref="ArtifactDigest.Compute"/>.</param>
    /// <param name="destinationFileSystem">Where the artifact is wanted; may differ from the cache's own filesystem.</param>
    /// <param name="destination">The path to write, whose extension selects the entry.</param>
    /// <returns><see langword="true"/> when the destination now holds the cached artifact.</returns>
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

    /// <summary>
    /// Takes a copy of <paramref name="source"/> into the cache under
    /// (<paramref name="kind"/>, <paramref name="key"/>). Failures are non-fatal by design.
    /// </summary>
    /// <remarks>
    /// The entry's extension comes from <paramref name="source"/>, which makes the cache
    /// browsable rather than a heap of hex — but it is part of the filename, so <b>a kind must
    /// use one extension consistently.</b> Storing under <c>.ktx2</c> and fetching with a
    /// <c>.png</c> destination misses silently forever rather than failing. A kind that cannot
    /// promise one extension should fold the extension into <paramref name="kind"/> instead.
    /// </remarks>
    /// <param name="kind">The artifact family, e.g. <c>ktx2</c>.</param>
    /// <param name="key">The digest from <see cref="ArtifactDigest.Compute"/>.</param>
    /// <param name="sourceFileSystem">Where the artifact currently is; may differ from the cache's own filesystem.</param>
    /// <param name="source">The freshly produced artifact.</param>
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
                // holds these very bytes, so keeping it is equivalent to overwriting — and it
                // avoids a window in which the entry is momentarily absent. (The Python side
                // uses os.replace here; Zio has no portable atomic replace-if-exists, and this
                // is the reason it does not need one.)
            }
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            Warn($"could not store '{source.GetName()}' ({error.Message})");
        }
        finally
        {
            // Gone already after a successful move; in every other outcome — a failed copy, a
            // lost race — it must not outlive the call.
            TryDeleteTemporary(temporary);
        }
    }

    /// <summary>Path of the entry for <paramref name="key"/>, taking its extension from <paramref name="like"/>.</summary>
    private bool TryGetEntryPath(string kind, string key, UPath like, out UPath entry)
    {
        entry = default;
        if (!_enabled) return false;
        Prepare();
        if (!_enabled) return false;

        entry = Root / kind / (key + like.GetExtensionWithDot());
        return true;
    }

    /// <summary>
    /// Creates the cache root, self-ignored, once per instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>.gitignore</c> holding <c>*</c> ignores the cache <i>and itself</i>, which leaves a
    /// clean <c>git status</c> in any project without that project having to know this directory
    /// exists. A build cache that dirties the working tree gets committed by accident exactly
    /// once.
    /// </para>
    /// <para>
    /// Locked because build steps run in parallel and the check-then-create here is otherwise a
    /// race whose loser sees "directory already exists" and concludes the root is unusable —
    /// turning the cache off for the rest of the build. <c>_prepared</c> is published after the
    /// work, so a thread taking the fast path sees a finished root.
    /// </para>
    /// </remarks>
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
            // Nothing useful to do: the caller's artifact is fine and the stray ".partial" is
            // inside a directory that is deleted wholesale.
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

    /// <summary>
    /// Expands a leading <c>~</c>, matching Python's <c>os.path.expanduser</c> so the same
    /// environment variable value means the same directory to both tools.
    /// </summary>
    private static string ExpandUser(string path)
    {
        if (path.Length == 0 || path[0] != '~') return path;
        if (path.Length > 1 && path[1] is not ('/' or '\\')) return path;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length == 0 ? path : home + path[1..];
    }

    /// <summary>
    /// The failures this type is allowed to swallow: everything the filesystem can refuse.
    /// A bug in a caller (a null argument, a malformed path) is not on the list and still throws.
    /// </summary>
    private static bool IsRecoverable(Exception error) => error is IOException or UnauthorizedAccessException;

    private void Warn(string message) => _warn?.Invoke($"Artifact cache: {message}.");
}
