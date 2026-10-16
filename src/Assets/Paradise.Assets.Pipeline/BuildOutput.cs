using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>Publishes a complete build artifact through a temporary sibling.</summary>
internal static class BuildOutput
{
    internal static void Publish(IFileSystem fileSystem, UPath destination, Action<Stream> write)
    {
        var temporary = destination.GetDirectory() / $".{destination.GetName()}.{Guid.NewGuid():N}.partial";
        try
        {
            using (var stream = fileSystem.OpenFile(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) write(stream);
            if (fileSystem.FileExists(destination)) fileSystem.ReplaceFile(temporary, destination, default, ignoreMetadataErrors: true);
            else fileSystem.MoveFile(temporary, destination);
        }
        finally
        {
            try
            {
                if (fileSystem.FileExists(temporary)) fileSystem.DeleteFile(temporary);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A later successful build sweeps an interrupted temporary export.
            }
        }
    }
}
