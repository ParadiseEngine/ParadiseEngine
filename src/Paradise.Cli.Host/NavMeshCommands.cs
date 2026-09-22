using System.Text;
using System.Text.Json;

using Paradise.Export.NavMesh;

using Zio;

namespace Paradise.Cli;

/// <summary>Runs navigation asset commands against a supplied filesystem without project discovery.</summary>
internal static class NavMeshCommands
{
    public static int Bake(IFileSystem fileSystem, UPath input, UPath output, UPath? preview = null, Action<string>? error = null)
    {
        try
        {
            ValidatePaths(input, output, preview);
            RequireNavMesh(output);
            var result = NavMeshBakeService.Bake(NavMeshBakeService.ReadInput(fileSystem.ReadAllText(input)));
            var previewBytes = preview.HasValue ? Encoding.UTF8.GetBytes(NavMeshBakeService.WritePreview(result.Preview)) : null;

            // Complete both products before replacing either destination; a failed preview write
            // must not publish a new navigation mesh that the editor cannot display.
            using var previewFile = preview.HasValue ? new StagedOutput(fileSystem, preview.Value) : null;
            previewFile?.Write(previewBytes!);
            using var binaryFile = new StagedOutput(fileSystem, output);
            binaryFile.Write(result.Bytes);
            previewFile?.Commit();
            binaryFile.Commit();
            return 0;
        }
        catch (Exception failure) when (IsCommandFailure(failure))
        {
            (error ?? Console.Error.WriteLine)($"paradise: bake-navmesh '{input}': {failure.Message}");
            return 1;
        }
    }

    public static int Preview(IFileSystem fileSystem, UPath input, UPath output, Action<string>? error = null)
    {
        try
        {
            ValidatePaths(input, output, null);
            RequireNavMesh(input);
            var preview = NavMeshBakeService.Preview(fileSystem.ReadAllBytes(input));
            var bytes = Encoding.UTF8.GetBytes(NavMeshBakeService.WritePreview(preview));
            using var previewFile = new StagedOutput(fileSystem, output);
            previewFile.Write(bytes);
            previewFile.Commit();
            return 0;
        }
        catch (Exception failure) when (IsCommandFailure(failure))
        {
            (error ?? Console.Error.WriteLine)($"paradise: preview-navmesh '{input}': {failure.Message}");
            return 1;
        }
    }

    private static bool IsCommandFailure(Exception failure) => failure is IOException or InvalidDataException
        or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException
        or NotSupportedException or OverflowException or IndexOutOfRangeException or NullReferenceException;

    private static void RequireNavMesh(UPath path)
    {
        if (!path.GetName().EndsWith(".navmesh", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The baked navigation mesh path must end in '.navmesh'.");
    }

    private static void ValidatePaths(UPath input, UPath output, UPath? preview)
    {
        RequireAbsoluteFile(input);
        RequireAbsoluteFile(output);
        if (preview.HasValue) RequireAbsoluteFile(preview.Value);
        if (SamePath(input, output) || (preview.HasValue && (SamePath(input, preview.Value) || SamePath(output, preview.Value))))
            throw new ArgumentException("Input, output and preview paths must be distinct.");
    }

    private static void RequireAbsoluteFile(UPath path)
    {
        if (!path.IsAbsolute || path == UPath.Root)
            throw new ArgumentException("Navigation asset paths must be absolute file paths.");
    }

    private static bool SamePath(UPath left, UPath right) =>
        string.Equals(left.FullName, right.FullName, StringComparison.OrdinalIgnoreCase);

    private sealed class StagedOutput(IFileSystem fileSystem, UPath destination) : IDisposable
    {
        private readonly UPath _temporary = destination.GetDirectory() / $".{destination.GetName()}.{Guid.NewGuid():N}.tmp";

        public void Write(byte[] bytes)
        {
            fileSystem.CreateDirectory(destination.GetDirectory());
            using var stream = fileSystem.OpenFile(_temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
        }

        public void Commit()
        {
            if (fileSystem.FileExists(destination)) fileSystem.ReplaceFile(_temporary, destination, default, false);
            else fileSystem.MoveFile(_temporary, destination);
        }

        public void Dispose()
        {
            try
            {
                if (fileSystem.FileExists(_temporary)) fileSystem.DeleteFile(_temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
