using System.ComponentModel;
using System.Diagnostics;

namespace Paradise.Cli;

/// <summary>Opens a directory in the OS file manager. Used by the tray's "open the build folder".</summary>
internal static class ShellFolders
{
    public static void Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {
            // Do not create the tree from a menu click. BuildRunner already creates the output
            // directory on a real rebuild; under --no-build a click must not write one.
            if (!Directory.Exists(path))
            {
                Console.Error.WriteLine($"watch: '{path}' does not exist yet");
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                return;
            }

            var start = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsMacOS() ? "open" : "xdg-open",
                UseShellExecute = false,
            };
            start.ArgumentList.Add(path);
            Process.Start(start);
        }
        catch (Exception error) when (error is Win32Exception
            or InvalidOperationException
            or FileNotFoundException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            Console.Error.WriteLine($"watch: could not open '{path}': {error.Message}");
        }
    }
}
