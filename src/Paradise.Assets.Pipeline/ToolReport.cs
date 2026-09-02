using System.Text.Json;

namespace Paradise.Assets.Pipeline;

public enum ToolStatus
{
    Ok,

    Missing,
}

public readonly record struct ToolFinding(string Name, ToolStatus Status, string? Version, string? Path, string? Fix);

/// <summary>
/// <c>tools doctor</c>. Exists because two tool failures were undiagnosable from what the build
/// said (a <c>slangc</c> quoting bug read as an absent compiler; a missing <c>ktx</c> on Windows
/// where the bootstrap cannot help). Uses the build's own probe (<see cref="KtxCreate.FindKtx"/>)
/// so the two cannot drift.
/// </summary>
public static class ToolReport
{
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
            var probe = KtxCreate.ProbeKtx(path);
            return probe.Usable
                ? new ToolFinding("ktx", ToolStatus.Ok, probe.VersionText?.Split(':', 2) is { Length: 2 } halves ? halves[1].Trim() : probe.VersionText, path, null)
                : new ToolFinding("ktx", ToolStatus.Missing, probe.VersionText, path, probe.Problem);
        }

        // On Windows the bootstrap cannot help: Khronos ships an NSIS installer needing elevation,
        // and a build must never raise a UAC prompt.
        var fix = OperatingSystem.IsWindows()
            ? "run 'paradise tools install ktx' (it will prompt for elevation — Khronos ships Windows as an installer, not an archive), "
              + "or install KTX-Software v5 yourself and set PARADISE_KTX_PATH to its bin/ktx.exe"
            : OperatingSystem.IsMacOS()
                ? "install KTX-Software v5 (Khronos ships macOS as a .pkg, which cannot be unpacked to a chosen directory) "
                  + "and set PARADISE_KTX_PATH to its bin/ktx; an engine checkout also vendors one under third_party/tools/KTX-Software"
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

        // Must match Slang.targets: $(NuGetPackageRoot)_slang/<version>/<rid>.
        var executable = OperatingSystem.IsWindows() ? "slangc.exe" : "slangc";
        var path = Path.Combine(ToolLocations.InstallRoot("slang", version), "bin", executable);

        return File.Exists(path)
            ? new ToolFinding("slangc", ToolStatus.Ok, Version(path, "-v") ?? version, path, null)
            : new ToolFinding("slangc", ToolStatus.Missing, null, null,
                "run 'paradise tools install slang', or just build the engine — the RestoreSlang target downloads it");
    }

    private static string? Version(string path, string flag)
    {
        var result = ProcessTools.Run(path, flag, timeoutMilliseconds: 20_000);
        if (!result.Started || result.TimedOut) return null;

        // Some tools answer on stderr, and ktx's exit code for --version is not dependable.
        var text = string.IsNullOrWhiteSpace(result.Stdout) ? result.Stderr : result.Stdout;
        var first = text.Split('\n').FirstOrDefault(line => line.Trim().Length > 0)?.Trim();

        return first?.Split(':', 2) is { Length: 2 } halves ? halves[1].Trim() : first;
    }
}
