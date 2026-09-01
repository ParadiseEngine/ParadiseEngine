using System.ComponentModel;
using System.Diagnostics;

namespace Paradise.Cli;

/// <summary>Opens a directory in the OS file manager. Used by the tray's "open the build folder".</summary>
internal static class ShellFolders
{
    public static void Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Directory.CreateDirectory(path);

        try
        {
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
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Console.Error.WriteLine($"watch: could not open '{path}': {error.Message}");
        }
    }
}
