using System.Security.Cryptography;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// The source tree as one importer sees it: every read and every existence check is recorded
/// with the stamp it had, so the index can later ask "would this importer see the same inputs
/// today" without knowing what the importer does with them.
/// </summary>
/// <remarks>
/// Mirrors <see cref="RecordingFileSystem"/> on the read side; together they make the index a
/// function of what actually happened rather than of a flag an importer had to get right
/// (issue #201). Three rules:
/// <list type="bullet">
/// <item>Under <c>assets/</c> the view is case- and normalisation-exact — a path is present only
/// when the directory walk returned that spelling — because the OS below may not be, and a build
/// that passed on macOS ships a reference Linux cannot open (issue #202).</item>
/// <item>The stamp is taken BEFORE the bytes are read. A write landing between the two leaves the
/// old stamp beside the new hash, and the next build's hash tier then reuses correctly; the
/// other order records a stamp the bytes never had.</item>
/// <item>Writes and directory listings are refused. A written source makes the tree
/// unreproducible; a listing is an input the index does not track, and an untracked input is
/// last week's artifact served with a green build.</item>
/// </list>
/// </remarks>
internal sealed class ObservedSources : ComposeFileSystem
{
    private readonly AssetPaths _sources;
    private readonly Dictionary<string, BuildInput> _records = new(StringComparer.Ordinal);

    public ObservedSources(IFileSystem fileSystem, AssetPaths sources)
        : base(fileSystem, owned: false)
    {
        _sources = sources;
    }

    /// <summary>In first-touch order; a read supersedes an existence check of the same path.</summary>
    public IReadOnlyList<BuildInput> Records => [.. _records.Values];

    /// <inheritdoc />
    protected override UPath ConvertPathToDelegate(UPath path) => path;

    /// <inheritdoc />
    protected override UPath ConvertPathFromDelegate(UPath path) => path;

    /// <inheritdoc />
    protected override bool FileExistsImpl(UPath path)
    {
        var exists = _sources.IsUnderRoot(path) ? _sources.Contains(path) : base.FileExistsImpl(path);
        NotePresence(path, exists);
        return exists;
    }

    /// <inheritdoc />
    protected override long GetFileLengthImpl(UPath path)
    {
        NotePresence(path, exists: true);
        return base.GetFileLengthImpl(path);
    }

    /// <inheritdoc />
    protected override DateTime GetLastWriteTimeImpl(UPath path)
    {
        NotePresence(path, exists: true);
        return base.GetLastWriteTimeImpl(path);
    }

    /// <inheritdoc />
    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        if ((access & FileAccess.Write) != 0 || mode is not (FileMode.Open or FileMode.OpenOrCreate))
        {
            throw new UnauthorizedAccessException($"'{path}': assets/ is read-only during a build; an importer that writes sources has made the tree unreproducible.");
        }

        // A miss is an input too: an importer that treats a companion file as optional must be
        // rebuilt when it appears.
        if (_sources.IsUnderRoot(path) && !_sources.Contains(path))
        {
            NotePresence(path, exists: false);
            throw new FileNotFoundException($"'{path}' does not exist under assets/ (references are case-exact).", path.FullName);
        }

        var stamp = FileStamp.Of(Fallback!, path);
        byte[] bytes;
        try
        {
            using var stream = base.OpenFileImpl(path, mode, access, share);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            NotePresence(path, exists: false);
            throw;
        }

        var key = BuildInput.KeyFor(_sources.Root, path);
        _records[key] = BuildInput.Content(key, stamp, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return new MemoryStream(bytes, writable: false);
    }

    private void NotePresence(UPath path, bool exists)
    {
        var key = BuildInput.KeyFor(_sources.Root, path);
        if (!_records.ContainsKey(key)) _records[key] = BuildInput.Presence(key, exists);
    }

    /// <inheritdoc />
    protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
        => throw new NotSupportedException(
            $"'{path}': an importer cannot list directories; a listing is an input the build index does not track, so reuse would serve stale output after a file is added.");
}
