using System.Text.Json;

namespace Paradise.Assets.Pipeline;

/// <summary>Whether a build tool is usable.</summary>
public enum ToolStatus
{
    /// <summary>Found and runnable.</summary>
    Ok,

    /// <summary>Not found anywhere the pipeline looks.</summary>
    Missing,
}

/// <summary>What <c>tools doctor</c> found out about one tool.</summary>
/// <param name="Name">The tool, as a person would name it.</param>
/// <param name="Status">Whether it is usable.</param>
/// <param name="Version">Its reported version, when it could be asked.</param>
/// <param name="Path">Where it was found.</param>
/// <param name="Fix">What to do about it, when something needs doing.</param>
public readonly record struct ToolFinding(string Name, ToolStatus Status, string? Version, string? Path, string? Fix);

/// <summary>
/// Reports on the tools a build shells out to.
/// </summary>
/// <remarks>
/// <para>
/// This exists because two tool failures in a row were undiagnosable from what the build said. A
/// <c>slangc exited with code -1</c> read as an absent compiler and was a quoting bug in the
/// MSBuild targets; a missing <c>ktx</c> said "install KTX-Software" without mentioning that on
/// Windows there is no unpackable archive and the bootstrap therefore cannot help. Both cost real
/// time that a straight answer to "what have I got, and what do I need" would have saved.
/// </para>
/// <para>
/// It resolves tools exactly the way the build does — <see cref="KtxCreate.FindKtx"/> is the same
/// probe the texture step uses — so a green report and a working build cannot disagree.
/// </para>
/// </remarks>
public static class ToolReport
{
    /// <summary>Reports on every tool the build needs.</summary>
    /// <param name="repoRoot">An engine checkout, for the vendored manifests. May not exist.</param>
    public static IReadOnlyList<ToolFinding> Collect(string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(repoRoot);
        return [Ktx(repoRoot), Slang(repoRoot)];
    }

    private static ToolFinding Ktx(string repoRoot)
    {
        var path = KtxCreate.FindKtx(repoRoot);
        if (path is not null)
        {
            return new ToolFinding("ktx", ToolStatus.Ok, Version(path, "--version"), path, null);
        }

        // The fix is platform-specific and that is the whole point of saying it here: on Linux the
        // bootstrap just works, and on Windows it cannot, because Khronos ships an NSIS installer
        // that requires elevation and a build must never raise a UAC prompt.
        var fix = OperatingSystem.IsWindows()
            ? "run 'paradise tools install ktx' (it will prompt for elevation — Khronos ships Windows as an installer, not an archive), "
              + "or install KTX-Software v5 yourself and set PARADISE_KTX_PATH to its bin/ktx.exe"
            : OperatingSystem.IsMacOS()
                ? "install KTX-Software v5 (Khronos ships macOS as a .pkg, which cannot be unpacked to a chosen directory) "
                  + "and set PARADISE_KTX_PATH to its bin/ktx"
                : "run 'paradise tools install ktx'";

        return new ToolFinding("ktx", ToolStatus.Missing, null, null, fix);
    }

    private static ToolFinding Slang(string repoRoot)
    {
        var manifest = Path.Combine(repoRoot, "tools", "slang", "slang.manifest.json");
        if (!File.Exists(manifest))
        {
            return new ToolFinding("slangc", ToolStatus.Missing, null, null,
                $"no slang manifest at '{manifest}' — slangc is restored by the engine's build, so this reports "
                + "only from an engine checkout");
        }

        string version;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            version = document.RootElement.GetProperty("version").GetString() ?? "";
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            return new ToolFinding("slangc", ToolStatus.Missing, null, null, $"could not read '{manifest}': {error.Message}");
        }

        // The same cache path Slang.targets computes: $(NuGetPackageRoot)_slang/<version>/<rid>.
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var executable = OperatingSystem.IsWindows() ? "slangc.exe" : "slangc";
        var path = Path.Combine(packages, "_slang", version, Rid(), "bin", executable);

        return File.Exists(path)
            ? new ToolFinding("slangc", ToolStatus.Ok, Version(path, "-v") ?? version, path, null)
            : new ToolFinding("slangc", ToolStatus.Missing, null, null,
                "run 'paradise tools install slang', or just build the engine — the RestoreSlang target downloads it");
    }

    private static string Rid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            .ToString().ToLowerInvariant();
        return $"{os}-{architecture}";
    }

    /// <summary>Whatever the tool prints for a version flag, or null if it will not run.</summary>
    private static string? Version(string path, string flag)
    {
        var result = ProcessTools.Run(path, flag, timeoutMilliseconds: 20_000);
        if (!result.Started || result.TimedOut) return null;

        // Some tools answer a version flag on stderr, and ktx's exit code for --version is not
        // something to depend on: what matters is that it printed something.
        var text = string.IsNullOrWhiteSpace(result.Stdout) ? result.Stderr : result.Stdout;
        var first = text.Split('\n').FirstOrDefault(line => line.Trim().Length > 0)?.Trim();

        // "ktx version: v5.0.0-rc2" -> "v5.0.0-rc2"; slangc prints the bare version already.
        return first?.Split(':', 2) is { Length: 2 } halves ? halves[1].Trim() : first;
    }
}
