namespace Paradise.Assets.Pipeline;

/// <summary>Shares tool install and lookup paths with the MSBuild bootstraps.</summary>
/// <remarks>Layout: <c>$(NuGetPackageRoot)_&lt;tool&gt;/&lt;version&gt;/&lt;rid&gt;</c>.</remarks>
public static class ToolLocations
{
    public static string PackagesRoot() =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    public static string HostRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            .ToString().ToLowerInvariant();
        return $"{os}-{architecture}";
    }

    public static string InstallRoot(string tool, string version) =>
        Path.Combine(PackagesRoot(), $"_{tool}", version, HostRid());

    /// <summary>
    /// Every installed version's root for this host, newest name first. The build has no manifest
    /// to read outside an engine checkout, so it takes whatever <c>tools install</c> left.
    /// </summary>
    public static IEnumerable<string> InstalledRoots(string tool)
    {
        var versions = Path.Combine(PackagesRoot(), $"_{tool}");
        if (!Directory.Exists(versions)) yield break;

        var rid = HostRid();
        foreach (var version in Directory.EnumerateDirectories(versions).OrderDescending(StringComparer.OrdinalIgnoreCase))
        {
            var root = Path.Combine(version, rid);
            if (Directory.Exists(root)) yield return root;
        }
    }
}
