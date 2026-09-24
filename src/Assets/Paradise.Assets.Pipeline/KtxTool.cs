using Paradise.Assets.Project;

namespace Paradise.Assets.Pipeline;

/// <summary>The KTX-Software v5 <c>ktx</c> executable: where it is, whether it can serve, and running it.</summary>
/// <remarks>
/// Nothing here decides HOW a texture is encoded; that is <see cref="TextureEncodePolicy"/>.
/// Nothing here touches a GLB; that is <see cref="GlbTextureRewriter"/>. Splitting the three
/// (issue #212) is what lets the build run the rewriter under Zio while this stays the only
/// place that spawns a process or touches a temp directory.
/// </remarks>
public static class KtxTool
{
    public const string PathEnvironmentVariable = "PARADISE_KTX_PATH";

    /// <summary>4.3 spelled <c>--assign-tf</c> as <c>--assign-oetf</c>; an older tool fails every texture with "unknown option", so it is refused up front.</summary>
    public static readonly Version MinimumVersion = new(4, 4);

    private const int EncodeTimeoutMilliseconds = 30 * 60 * 1000;
    private const int ProbeTimeoutMilliseconds = 30_000;

    /// <summary>
    /// <c>PARADISE_KTX_PATH</c>, then a vendored <c>third_party/tools/KTX-Software</c> under the
    /// root, then what <c>paradise tools install ktx</c> put under the packages root, then PATH.
    /// The same order as <c>tools doctor</c> reports, because it is the same call.
    /// </summary>
    public static string? Find(string? repoRoot = null) =>
        ProcessTools.FindExecutable(
            Environment.GetEnvironmentVariable(PathEnvironmentVariable),
            RepositoryPaths(repoRoot).Concat(InstalledPaths()),
            "ktx");

    public readonly record struct ProbeResult(string Path, string? VersionText, Version? Version, string? Problem)
    {
        public bool Usable => Problem is null;
    }

    /// <summary>Runs <c>ktx --version</c> once: a tool that is present but cannot run, or is older than <see cref="MinimumVersion"/>, is reported rather than failing per texture.</summary>
    public static ProbeResult Probe(string ktxPath)
    {
        var run = ProcessTools.Run(ktxPath, "--version", ProbeTimeoutMilliseconds, LoaderEnvironment(ktxPath));
        if (!run.Started || run.TimedOut)
        {
            return new ProbeResult(ktxPath, null, null, run.Describe($"ktx at '{ktxPath}'", ProbeTimeoutMilliseconds).Trim());
        }

        // ktx answers --version on stdout or stderr depending on the build, and its exit code is not dependable.
        var text = (string.IsNullOrWhiteSpace(run.Stdout) ? run.Stderr : run.Stdout).Trim();
        if (!TryParseVersion(text, out var version))
        {
            return new ProbeResult(ktxPath, text, null, $"ktx at '{ktxPath}' answered '--version' with '{text}', which names no version; KTX-Software v{MinimumVersion}+ is required.");
        }

        return version < MinimumVersion
            ? new ProbeResult(ktxPath, text, version, $"ktx at '{ktxPath}' is v{version}; KTX-Software v{MinimumVersion}+ is required (older builds spell the encode options differently). Install a newer one and set {PathEnvironmentVariable}, or run 'paradise tools install ktx'.")
            : new ProbeResult(ktxPath, text, version, null);
    }

    /// <summary>Reads the leading numeric run of a version line such as <c>ktx version: v5.0.0-rc1~5</c>.</summary>
    public static bool TryParseVersion(string versionText, out Version? version)
    {
        ArgumentNullException.ThrowIfNull(versionText);

        version = null;
        var digits = -1;
        for (var i = 0; i + 1 < versionText.Length; i++)
        {
            if ((versionText[i] == 'v' || versionText[i] == 'V') && char.IsDigit(versionText[i + 1]))
            {
                digits = i + 1;
                break;
            }
        }

        if (digits < 0)
        {
            digits = versionText.AsSpan().IndexOfAnyInRange('0', '9');
            if (digits < 0) return false;
        }

        var end = digits;
        while (end < versionText.Length && (char.IsDigit(versionText[end]) || versionText[end] == '.')) end++;

        var numeric = versionText[digits..end].TrimEnd('.');
        if (!numeric.Contains('.')) numeric += ".0";

        return Version.TryParse(numeric, out version);
    }

    /// <summary>Bytes in, KTX2 bytes out, through a private temp directory; caching and placement are the caller's business.</summary>
    public static bool TryEncode(
        string ktxPath,
        byte[] source,
        string sourceExtension,
        TextureEncodingPreset preset,
        TextureQuality quality,
        out byte[] ktx2,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(ktxPath);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceExtension);

        ktx2 = [];
        error = "";
        var tempDirectory = Path.Combine(Path.GetTempPath(), "ParadiseKtx2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var sourcePath = Path.Combine(tempDirectory, "source" + sourceExtension);
            var outputPath = Path.Combine(tempDirectory, "texture.ktx2");
            File.WriteAllBytes(sourcePath, source);

            var run = ProcessTools.Run(
                ktxPath,
                TextureEncodePolicy.CreateArguments(preset, outputPath, sourcePath, quality),
                EncodeTimeoutMilliseconds,
                LoaderEnvironment(ktxPath));

            if (!run.Succeeded)
            {
                error = run.Describe("ktx create", EncodeTimeoutMilliseconds);
                return false;
            }

            if (!File.Exists(outputPath))
            {
                error = $"ktx create exited 0 but wrote no '{outputPath}'.\n{run.Stdout}{run.Stderr}";
                return false;
            }

            var produced = File.ReadAllBytes(outputPath);
            if (!Ktx2Header.IsValid(produced, out var validationError))
            {
                error = $"ktx create produced an invalid KTX2 texture: {validationError}";
                return false;
            }

            ktx2 = produced;
            return true;
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // Point the dynamic loader at the libktx shipped next to the ktx binary (the release
    // archives and the vendored macOS build both lay out bin/ktx beside lib/libktx.*).
    internal static IReadOnlyDictionary<string, string>? LoaderEnvironment(string ktxPath)
    {
        if (OperatingSystem.IsWindows()) return null;

        var ktxDirectory = Path.GetDirectoryName(ktxPath);
        if (string.IsNullOrWhiteSpace(ktxDirectory)) return null;

        var libDirectory = Path.GetFullPath(Path.Combine(ktxDirectory, "..", "lib"));
        if (!Directory.Exists(libDirectory)) return null;

        var env = new Dictionary<string, string>();
        string[] variables = OperatingSystem.IsMacOS()
            ? ["DYLD_LIBRARY_PATH", "DYLD_FALLBACK_LIBRARY_PATH"]
            : ["LD_LIBRARY_PATH"];
        foreach (var variable in variables)
        {
            var existing = Environment.GetEnvironmentVariable(variable);
            env[variable] = string.IsNullOrWhiteSpace(existing) ? libDirectory : libDirectory + Path.PathSeparator + existing;
        }

        return env;
    }

    private static string FileName => OperatingSystem.IsWindows() ? "ktx.exe" : "ktx";

    // The vendored tree is keyed by the platform the archive was built for (Darwin-arm64,
    // Linux-x86_64, Windows-x64), so only this host's family is offered: enumerating every
    // ktx returned the macOS binary on Linux CI.
    private static IEnumerable<string> RepositoryPaths(string? repoRoot)
    {
        var root = Path.GetFullPath(Path.Combine(repoRoot ?? Directory.GetCurrentDirectory(), "third_party", "tools", "KTX-Software"));
        if (!Directory.Exists(root)) yield break;

        yield return Path.Combine(root, "bin", FileName);

        var platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "Darwin" : "Linux";
        foreach (var directory in Directory.EnumerateDirectories(root, platform + "-*").Order(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(directory, "bin", FileName);
        }
    }

    private static IEnumerable<string> InstalledPaths()
    {
        foreach (var root in ToolLocations.InstalledRoots("ktx"))
        {
            yield return Path.Combine(root, "bin", FileName);
        }
    }
}
