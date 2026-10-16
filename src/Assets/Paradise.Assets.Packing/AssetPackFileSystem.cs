using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Packing;

/// <summary>A read-only filesystem with seekable, independently decoded asset streams.</summary>
/// <remarks>
/// The backing pack must remain immutable while mounted. Storage is borrowed and must outlive
/// its readers; memory is retained without copying. Open streams own their cursors and survive
/// mount disposal. Each stream follows the usual Stream contract and is not itself thread-safe.
/// </remarks>
public sealed class AssetPackFileSystem : FileSystem
{
    private readonly Func<Stream> _open;
    private readonly PackIndex _index;
    private readonly Dictionary<UPath, PackEntry> _files;
    private readonly HashSet<UPath> _directories = [UPath.Root];

    public AssetPackFileSystem(IFileSystem storage, UPath path) : this(CreateOpener(storage, path)) { }

    public AssetPackFileSystem(ReadOnlyMemory<byte> bytes) : this(() => new ReadOnlyMemoryStream(bytes)) { }

    private AssetPackFileSystem(Func<Stream> open)
    {
        _open = open;
        using var input = open();
        _index = PackFormat.ReadIndex(input);
        _files = new Dictionary<UPath, PackEntry>(_index.Entries.Length);
        foreach (var entry in _index.Entries)
        {
            var path = new UPath("/" + entry.Path);
            _files.Add(path, entry);
            for (var parent = path.GetDirectory(); parent != UPath.Root; parent = parent.GetDirectory()) _directories.Add(parent);
        }
    }

    private static Func<Stream> CreateOpener(IFileSystem storage, UPath path)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (!path.IsAbsolute) throw new ArgumentException("Pack path must be absolute.", nameof(path));
        return () => storage.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        if (mode != FileMode.Open || access != FileAccess.Read) throw ReadOnly();
        var entry = GetFile(path);
        var input = _open();
        try { return new AssetPackStream(input, _index, entry); }
        catch { input.Dispose(); throw; }
    }

    protected override bool FileExistsImpl(UPath path) => _files.ContainsKey(path);
    protected override bool DirectoryExistsImpl(UPath path) => _directories.Contains(path);
    protected override long GetFileLengthImpl(UPath path) => GetFile(path).Length;
    protected override FileAttributes GetAttributesImpl(UPath path)
    {
        if (_directories.Contains(path)) return FileAttributes.Directory | FileAttributes.ReadOnly;
        _ = GetFile(path);
        return FileAttributes.ReadOnly;
    }

    protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
    {
        var pattern = SearchPattern.Parse(ref path, ref searchPattern);
        if (!_directories.Contains(path)) throw new DirectoryNotFoundException(path.FullName);
        return Enumerate(path, searchOption)
            .Where(candidate => pattern.Match(candidate) && (searchTarget == SearchTarget.Both
                || (searchTarget == SearchTarget.Directory ? _directories.Contains(candidate) : _files.ContainsKey(candidate))));
    }

    protected override IEnumerable<FileSystemItem> EnumerateItemsImpl(UPath path, SearchOption searchOption, SearchPredicate? searchPredicate)
    {
        if (!_directories.Contains(path)) throw new DirectoryNotFoundException(path.FullName);
        foreach (var candidate in Enumerate(path, searchOption))
        {
            bool directory = _directories.Contains(candidate);
            var item = new FileSystemItem(this, candidate, directory)
            {
                Attributes = FileAttributes.ReadOnly | (directory ? FileAttributes.Directory : 0),
                Length = directory ? 0 : _files[candidate].Length,
                CreationTime = DateTimeOffset.UnixEpoch,
                LastAccessTime = DateTimeOffset.UnixEpoch,
                LastWriteTime = DateTimeOffset.UnixEpoch,
            };
            if (searchPredicate is null || searchPredicate(ref item)) yield return item;
        }
    }

    private IEnumerable<UPath> Enumerate(UPath path, SearchOption option)
        => _directories.Concat(_files.Keys)
            .Where(candidate => candidate != path && (option == SearchOption.TopDirectoryOnly
                ? candidate.GetDirectory() == path : candidate.IsInDirectory(path, recursive: true)))
            .OrderBy(candidate => candidate.FullName, StringComparer.Ordinal);

    private PackEntry GetFile(UPath path)
        => _files.TryGetValue(path, out var entry) ? entry : throw new FileNotFoundException($"No packed asset '{path}'.", path.FullName);

    private DateTime GetTime(UPath path) { _ = GetAttributesImpl(path); return DateTime.UnixEpoch; }
    private static IOException ReadOnly() => new("Asset packs are read-only.");
    protected override DateTime GetCreationTimeImpl(UPath path) => GetTime(path);
    protected override DateTime GetLastAccessTimeImpl(UPath path) => GetTime(path);
    protected override DateTime GetLastWriteTimeImpl(UPath path) => GetTime(path);
    protected override bool CanWatchImpl(UPath path) => false;
    protected override IFileSystemWatcher WatchImpl(UPath path) => throw new NotSupportedException("Asset packs cannot be watched.");
    protected override string ConvertPathToInternalImpl(UPath path) => path.FullName;
    protected override UPath ConvertPathFromInternalImpl(string path) => new(path);
    protected override bool TryResolveLinkTargetImpl(UPath linkPath, out UPath resolvedPath) { resolvedPath = default; return false; }
    protected override void CreateDirectoryImpl(UPath path) => throw ReadOnly();
    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath) => throw ReadOnly();
    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive) => throw ReadOnly();
    protected override void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite) => throw ReadOnly();
    protected override void ReplaceFileImpl(UPath srcPath, UPath destPath, UPath destBackupPath, bool ignoreMetadataErrors) => throw ReadOnly();
    protected override void MoveFileImpl(UPath srcPath, UPath destPath) => throw ReadOnly();
    protected override void DeleteFileImpl(UPath path) => throw ReadOnly();
    protected override void SetAttributesImpl(UPath path, FileAttributes attributes) => throw ReadOnly();
    protected override void SetCreationTimeImpl(UPath path, DateTime time) => throw ReadOnly();
    protected override void SetLastAccessTimeImpl(UPath path, DateTime time) => throw ReadOnly();
    protected override void SetLastWriteTimeImpl(UPath path, DateTime time) => throw ReadOnly();
    protected override void CreateSymbolicLinkImpl(UPath path, UPath pathToTarget) => throw ReadOnly();
}
