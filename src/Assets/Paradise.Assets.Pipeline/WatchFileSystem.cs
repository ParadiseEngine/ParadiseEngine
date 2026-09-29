using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline;

/// <summary>Records pipeline mutations for the watcher's live inventory without waiting for OS events.</summary>
internal sealed class WatchFileSystem(IFileSystem fallback, Action<UPath> changed) : ComposeFileSystem(fallback, owned: false)
{
    protected override UPath ConvertPathToDelegate(UPath path) => path;

    protected override UPath ConvertPathFromDelegate(UPath path) => path;

    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        var stream = base.OpenFileImpl(path, mode, access, share);
        if ((access & FileAccess.Write) != 0) changed(path);
        return stream;
    }

    protected override void DeleteFileImpl(UPath path)
    {
        base.DeleteFileImpl(path);
        changed(path);
    }

    protected override void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite)
    {
        base.CopyFileImpl(srcPath, destPath, overwrite);
        changed(destPath);
    }

    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
    {
        base.MoveFileImpl(srcPath, destPath);
        changed(srcPath);
        changed(destPath);
    }

    protected override void ReplaceFileImpl(UPath srcPath, UPath destPath, UPath destBackupPath, bool ignoreMetadataErrors)
    {
        base.ReplaceFileImpl(srcPath, destPath, destBackupPath, ignoreMetadataErrors);
        changed(srcPath);
        changed(destPath);
        if (!destBackupPath.IsNull && !destBackupPath.IsEmpty) changed(destBackupPath);
    }
}
