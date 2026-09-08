using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline;

/// <summary>Confines importer output to the build tree and records completed writes.</summary>
/// <remarks>
/// Whole-file writes use a temporary sibling renamed on close. Failed writes are discarded;
/// interrupted builds leave <c>.partial</c> files for cleanup instead of truncated output.
/// Append and open-existing modes pass through because they depend on existing contents.
/// </remarks>
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
        if ((access & FileAccess.Write) == 0) return base.OpenFileImpl(path, mode, access, share);

        RecordWrite(path);
        if (mode is not (FileMode.Create or FileMode.CreateNew or FileMode.Truncate))
        {
            return base.OpenFileImpl(path, mode, access, share);
        }

        var temporary = path.GetDirectory() / $"{path.GetName()}.{Guid.NewGuid():N}.partial";
        return new CommitOnClose(this, base.OpenFileImpl(temporary, FileMode.Create, access, share), temporary, path);
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

    private void Commit(UPath temporary, UPath destination)
    {
        // Zio has no move-with-overwrite; ReplaceFile is atomic where the OS can be, and needs
        // an existing destination.
        if (base.FileExistsImpl(destination)) base.ReplaceFileImpl(temporary, destination, default, ignoreMetadataErrors: true);
        else base.MoveFileImpl(temporary, destination);
    }

    private void Discard(UPath temporary)
    {
        try
        {
            if (base.FileExistsImpl(temporary)) base.DeleteFileImpl(temporary);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The sweep at the end of the next successful build takes it.
        }
    }

    private sealed class CommitOnClose(RecordingFileSystem owner, Stream inner, UPath temporary, UPath destination) : Stream
    {
        private bool _faulted;
        private bool _closed;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            try
            {
                inner.Write(buffer, offset, count);
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            try
            {
                inner.Write(buffer);
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                inner.Dispose();
                if (_faulted) owner.Discard(temporary);
                else owner.Commit(temporary, destination);
            }

            base.Dispose(disposing);
        }
    }
}
