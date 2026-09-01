using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// The output mount an importer writes to: a sub-filesystem rooted at the build tree that
/// OBSERVES writes.
/// </summary>
/// <remarks>
/// <para>
/// Two jobs, both structural rather than conventional. The mount is the capability: <c>/</c>
/// here is the output directory, so an importer cannot write outside the tree it is building —
/// no path discipline required. And the observation is the record: <see cref="Written"/> is
/// derived from the writes that actually happened, so the build manifest cannot drift from
/// reality the way an importer-reported file list could.
/// </para>
/// <para>
/// A write's parent directories are created on demand, so an importer's output is one
/// <c>WriteAllBytes</c> with no ceremony.
/// </para>
/// </remarks>
internal sealed class RecordingFileSystem : SubFileSystem
{
    private readonly List<UPath> _written = [];

    /// <summary>Mounts <paramref name="root"/> (which must exist) on <paramref name="fileSystem"/>.</summary>
    public RecordingFileSystem(IFileSystem fileSystem, UPath root)
        : base(fileSystem, root, owned: false)
    {
    }

    /// <summary>The files written through this mount, mount-relative, in first-write order.</summary>
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
