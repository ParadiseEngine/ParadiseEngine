using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline;

/// <summary>The importer's output mount: rooted at the build tree so an importer cannot write outside it, and recording writes so the manifest cannot drift from what actually happened.</summary>
internal sealed class RecordingFileSystem : SubFileSystem
{
    private readonly List<UPath> _written = [];

    public RecordingFileSystem(IFileSystem fileSystem, UPath root)
        : base(fileSystem, root, owned: false)
    {
    }

    /// <summary>Mount-relative, in first-write order.</summary>
    public IReadOnlyList<UPath> Written => _written;

    /// <inheritdoc />
    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        if ((access & FileAccess.Write) != 0) RecordWrite(path);
        return base.OpenFileImpl(path, mode, access, share);
    }

    /// <inheritdoc />
    protected override void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite)
    {
        RecordWrite(destPath);
        base.CopyFileImpl(srcPath, destPath, overwrite);
    }

    /// <inheritdoc />
    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
    {
        RecordWrite(destPath);
        base.MoveFileImpl(srcPath, destPath);
    }

    /// <inheritdoc />
    protected override void ReplaceFileImpl(UPath srcPath, UPath destPath, UPath destBackupPath, bool ignoreMetadataErrors)
    {
        RecordWrite(destPath);
        base.ReplaceFileImpl(srcPath, destPath, destBackupPath, ignoreMetadataErrors);
    }

    private void RecordWrite(UPath path)
    {
        var parent = path.GetDirectory();
        if (!parent.IsNull && !parent.IsEmpty && !DirectoryExists(parent)) CreateDirectory(parent);
        if (!_written.Contains(path)) _written.Add(path);
    }
}
