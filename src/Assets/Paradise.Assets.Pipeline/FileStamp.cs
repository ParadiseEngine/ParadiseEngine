using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>(mtime, size): the cheap tier of "has this file changed", shared by the index and the sidecar maintainer so both hash only when it fails.</summary>
internal static class FileStamp
{
    public static (long Mtime, long Size)? Of(IFileSystem fileSystem, UPath path)
    {
        try
        {
            if (!fileSystem.FileExists(path)) return null;
            return (fileSystem.GetLastWriteTime(path).ToUniversalTime().Ticks, fileSystem.GetFileLength(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
