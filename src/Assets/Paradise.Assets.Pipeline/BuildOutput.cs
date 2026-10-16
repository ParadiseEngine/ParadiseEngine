using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>Publishes a complete build artifact through a temporary sibling.</summary>
internal static class BuildOutput
{
    internal static void Publish(IFileSystem fileSystem, UPath destination, Action<Stream> write)
    {
        var temporary = destination.GetDirectory() / $".{destination.GetName()}.{Guid.NewGuid():N}.partial";
        var backup = destination.GetDirectory() / $".{destination.GetName()}.{Guid.NewGuid():N}.backup.partial";
        bool complete = false;
        bool published = false;
        try
        {
            using (var stream = fileSystem.OpenFile(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) write(stream);
            complete = true;
            bool hadDestination = fileSystem.FileExists(destination);
            try
            {
                // Zio's physical ReplaceFile overwrites by copying, which can truncate the old artifact.
                if (hadDestination) fileSystem.MoveFile(destination, backup);
                fileSystem.MoveFile(temporary, destination);
            }
            catch (Exception publicationError) when (publicationError is not OutOfMemoryException)
            {
                try
                {
                    if (fileSystem.FileExists(backup))
                    {
                        if (fileSystem.FileExists(destination)) fileSystem.DeleteFile(destination);
                        fileSystem.MoveFile(backup, destination);
                    }
                    else if (!hadDestination && fileSystem.FileExists(destination)) fileSystem.DeleteFile(destination);
                }
                catch (Exception rollbackError) when (rollbackError is not OutOfMemoryException)
                {
                    throw new IOException($"Publication and rollback failed for '{destination}'; recovery files: '{backup}', '{temporary}'.",
                        new AggregateException(publicationError, rollbackError));
                }

                throw;
            }

            published = true;
            TryDelete(fileSystem, backup);
        }
        finally
        {
            // Keep a complete candidate after failed publication, including failed rollback.
            if (!complete || published) TryDelete(fileSystem, temporary);
        }
    }

    internal static void TryDelete(IFileSystem fileSystem, UPath path)
    {
        try
        {
            if (fileSystem.FileExists(path)) fileSystem.DeleteFile(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A later full build sweeps interrupted temporary exports.
        }
    }
}
