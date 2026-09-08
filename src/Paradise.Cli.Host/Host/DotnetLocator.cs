namespace Paradise.Cli;

/// <summary>Finds <c>dotnet</c> through PATH, <c>DOTNET_ROOT</c> and installer directories.</summary>
/// <remarks><see cref="Environment.ProcessPath"/> may name the CLI apphost rather than the dotnet muxer.</remarks>
internal static class DotnetLocator
{
    private static readonly string s_executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    public static string? Find()
    {
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, s_executable))
            .FirstOrDefault(File.Exists);
        if (onPath is not null) return onPath;

        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
        {
            candidates.Add(Path.Combine(root, s_executable));
        }

        candidates.Add("/usr/local/share/dotnet/dotnet");
        candidates.Add("/opt/homebrew/bin/dotnet");
        candidates.Add("/usr/share/dotnet/dotnet");
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", s_executable));
        if (OperatingSystem.IsWindows())
        {
            // The installer's machine-wide location, then the per-user one dotnet-install.ps1 uses.
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", s_executable));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", s_executable));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet", s_executable));
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}
