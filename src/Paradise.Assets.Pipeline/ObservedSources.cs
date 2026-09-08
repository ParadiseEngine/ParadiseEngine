using System.Security.Cryptography;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline;

/// <summary>Records importer reads and existence checks for incremental build reuse.</summary>
/// <remarks>
/// Paths beneath <c>assets/</c> must match the index's exact case and normalization.
/// Take the stamp before reading bytes so concurrent writes cannot associate old bytes with a new stamp.
/// Writes are forbidden; directory listings are also forbidden because the index cannot track them.
/// </remarks>
internal sealed class ObservedSources : ComposeFileSystem
{
    private readonly AssetIndex _sources;
    private readonly Dictionary<string, BuildInput> _records = new(StringComparer.Ordinal);

    public ObservedSources(IFileSystem fileSystem, AssetIndex sources)
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
