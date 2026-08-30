// Console app built by tools/ktx/KtxBootstrap.csproj — the KTX-Software twin of
// tools/slang/SlangBootstrap.cs (same download → SHA256-verify → extract → marker shape, same
// cross-process lock; see that file for the history behind each of those decisions). Resolves a
// `ktx` CLI archive from tools/ktx/ktx.manifest.json for a given RID and installs it under the
// cache directory the caller names — typically third_party/tools/KTX-Software, which
// KtxCreate.FindKtx already probes.
//
// The manifest carries every RID Khronos publishes a usable asset for. Linux is a tarball and
// extracts unattended, which is what CI runs. WINDOWS is an NSIS installer that REQUIRES
// ELEVATION: it is gated behind --elevate and refused otherwise, because a build must never raise
// a UAC prompt — that would hang an unattended run and ambush anyone who only typed `dotnet
// build`. macOS ships a .pkg with no directory-targeted silent install, so darwin RIDs are absent
// and this exits with guidance instead: install KTX-Software and set PARADISE_KTX_PATH.
//
// Args:
//   --manifest <path>   tools/ktx/ktx.manifest.json
//   --rid <rid>         e.g. linux-x64
//   --out <dir>         destination cache directory (parent of bin/ktx)
//   --elevate           permit a format that needs admin (only `paradise tools install` passes it)
//
// Exit codes: 0 = success / already-installed, 1 = failure (SHA mismatch, missing RID, network,
// or an elevation-needing format without --elevate).

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

string? manifestPath = null;
string? rid = null;
string? outDir = null;
var elevate = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--manifest" when i + 1 < args.Length: manifestPath = args[++i]; break;
        case "--rid" when i + 1 < args.Length: rid = args[++i]; break;
        case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
        case "--elevate": elevate = true; break;
    }
}

if (manifestPath is null || rid is null || outDir is null)
{
    Console.Error.WriteLine("Usage: KtxBootstrap --manifest <path> --rid <rid> --out <dir> [--elevate]");
    return 1;
}

using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
var root = doc.RootElement;
if (!root.GetProperty("rids").TryGetProperty(rid, out var entry))
{
    Console.Error.WriteLine(
        $"KTX manifest at '{manifestPath}' has no entry for RID '{rid}'. Khronos ships no " +
        "extractable archive for this platform — install KTX-Software " +
        "(https://github.com/KhronosGroup/KTX-Software/releases) and point PARADISE_KTX_PATH " +
        "at its bin/ktx executable.");
    return 1;
}

var url = entry.GetProperty("url").GetString()!;
var expectedSha = entry.GetProperty("sha256").GetString()!;
var format = entry.GetProperty("format").GetString()!;

Directory.CreateDirectory(outDir);
var markerPath = Path.Combine(outDir, ".installed");
// The executable's name follows the ARCHIVE's platform (the RID), not the host's — which also
// lets a cross-RID invocation (e.g. priming a Linux CI cache from elsewhere) verify layout.
var ktxName = rid.StartsWith("win", StringComparison.Ordinal) ? "ktx.exe" : "ktx";
var ktxPath = Path.Combine(outDir, "bin", ktxName);

// Cross-process lock; see SlangBootstrap.cs for the race this closes.
var lockDir = Path.GetDirectoryName(outDir) ?? outDir;
Directory.CreateDirectory(lockDir);
var lockPath = Path.Combine(lockDir, ".ktx-bootstrap.lock");
FileStream? lockHandle = null;
var lockAcquireDeadline = DateTime.UtcNow.AddMinutes(15);
while (true)
{
    try
    {
        lockHandle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        break;
    }
    catch (IOException)
    {
        if (DateTime.UtcNow > lockAcquireDeadline)
        {
            Console.Error.WriteLine($"Timed out waiting on KTX bootstrap lock at '{lockPath}'.");
            return 1;
        }
        await Task.Delay(250);
    }
}
using var _lock = lockHandle;

if (File.Exists(markerPath) && File.Exists(ktxPath))
{
    var existing = File.ReadAllText(markerPath).Trim();
    if (string.Equals(existing, expectedSha, StringComparison.OrdinalIgnoreCase))
    {
        return 0; // Already installed at the requested SHA.
    }
}

Console.WriteLine($"Downloading ktx from {url}");
var archivePath = Path.Combine(outDir, "ktx-archive." + format);
using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
{
    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
    resp.EnsureSuccessStatusCode();
    await using var net = await resp.Content.ReadAsStreamAsync();
    await using var fs = File.Create(archivePath);
    await net.CopyToAsync(fs);
}

string actualSha;
using (var sha = SHA256.Create())
await using (var fs = File.OpenRead(archivePath))
{
    var bytes = await sha.ComputeHashAsync(fs);
    actualSha = Convert.ToHexString(bytes).ToLowerInvariant();
}
if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"KTX archive SHA256 mismatch: expected '{expectedSha}', got '{actualSha}' (source: {url}).");
    try { File.Delete(archivePath); } catch { }
    return 1;
}

var stagingDir = outDir + ".extracting";
if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
Directory.CreateDirectory(stagingDir);

if (string.Equals(format, "zip", StringComparison.OrdinalIgnoreCase))
{
    ZipFile.ExtractToDirectory(archivePath, stagingDir);
}
else if (format is "tar.gz" or "tar.bz2")
{
    // The platform tar (`-xf` auto-detects the compression), for the same PAX-header reason as
    // the Slang bootstrap — and because .NET has no bzip2 reader at all.
    using var tar = Process.Start(new ProcessStartInfo("tar")
    {
        ArgumentList = { "-xf", archivePath, "-C", stagingDir },
        UseShellExecute = false,
        RedirectStandardError = true,
    });
    if (tar is null)
    {
        Console.Error.WriteLine("Failed to start 'tar' to extract the KTX archive.");
        return 1;
    }
    var tarErr = await tar.StandardError.ReadToEndAsync();
    await tar.WaitForExitAsync();
    if (tar.ExitCode != 0)
    {
        Console.Error.WriteLine($"'tar' failed to extract '{archivePath}' (exit {tar.ExitCode}): {tarErr}");
        return 1;
    }
}
else if (string.Equals(format, "nsis", StringComparison.OrdinalIgnoreCase))
{
    // Khronos publishes no Windows ARCHIVE — only an NSIS installer — and no unpacker handles
    // modern NSIS payloads. The installer's own silent mode writes exactly the files an archive
    // would have contained into a directory we choose, so that is what this uses.
    //
    // IT REQUIRES ELEVATION, which is why it is behind a flag. A build must never raise a UAC
    // prompt: it would block an unattended CI run forever and ambush a developer who only typed
    // `dotnet build`. So an unflagged run refuses and says who to ask instead. `paradise tools
    // install ktx` passes --elevate, because there a person has just asked for this by name.
    if (!elevate)
    {
        Console.Error.WriteLine(
            $"'{rid}' ships an NSIS installer, which requires elevation. A build will not prompt for it — " +
            "run 'paradise tools install ktx' (it will ask), or install KTX-Software yourself and set " +
            "PARADISE_KTX_PATH to its bin/ktx.exe.");
        try { File.Delete(archivePath); } catch { }
        return 1;
    }

    // /D is NSIS's target directory and has two rules that are not negotiable: it must be the
    // LAST argument, and it must be UNQUOTED, because NSIS takes the rest of the command line
    // verbatim. That is also why a path with spaces works here and why this uses the raw
    // Arguments string -- ArgumentList would quote it and NSIS would install to the default
    // location instead, silently, leaving the cache empty.
    //
    // UseShellExecute is what makes elevation possible at all: without it, CreateProcess fails
    // with error 740 on a manifest that demands admin rather than showing the prompt.
    using var installer = Process.Start(new ProcessStartInfo(archivePath)
    {
        Arguments = $"/S /D={Path.GetFullPath(stagingDir)}",
        UseShellExecute = true,
        Verb = "runas",
    });

    if (installer is null)
    {
        Console.Error.WriteLine($"Failed to start the KTX installer '{archivePath}'.");
        return 1;
    }

    await installer.WaitForExitAsync();
    if (installer.ExitCode != 0)
    {
        Console.Error.WriteLine($"The KTX installer failed (exit {installer.ExitCode}): {archivePath}");
        return 1;
    }

    // A silent NSIS run reports success before its own files have all landed in some cases, and
    // an empty staging directory would otherwise be promoted over a good cache.
    if (!File.Exists(Path.Combine(stagingDir, "bin", "ktx.exe")))
    {
        Console.Error.WriteLine($"The KTX installer reported success but produced no bin/ktx.exe under '{stagingDir}'.");
        return 1;
    }
}
else
{
    Console.Error.WriteLine($"Unsupported KTX archive format '{format}' (expected 'zip', 'tar.gz', 'tar.bz2' or 'nsis').");
    return 1;
}

try { File.Delete(archivePath); } catch { }

// KTX tarballs unpack into a single top-level directory (KTX-Software-5.0.0-rc2-Linux-x86_64/);
// promote its contents so <out>/bin/ktx resolves uniformly.
//
// NOT for an installer, which already writes bin/ + lib/ + share/ at the top level. The collapse
// is keyed on "exactly one directory", and an installer that happened to lay down only bin/ would
// be flattened into <out>/ktx.exe — leaving <out>/bin/ktx.exe missing, which is the one path
// every caller resolves. Excluding the format is cheaper than depending on how many directories
// a future release ships.
var stagedEntries = Directory.GetFileSystemEntries(stagingDir);
string promoteRoot = stagingDir;
if (!string.Equals(format, "nsis", StringComparison.OrdinalIgnoreCase)
    && stagedEntries.Length == 1 && Directory.Exists(stagedEntries[0]))
{
    promoteRoot = stagedEntries[0];
}

foreach (var existing in Directory.GetFileSystemEntries(outDir))
{
    if (Path.GetFileName(existing) == ".installed") continue;
    try
    {
        if (Directory.Exists(existing)) Directory.Delete(existing, recursive: true);
        else File.Delete(existing);
    }
    catch { }
}

foreach (var promoted in Directory.GetFileSystemEntries(promoteRoot))
{
    var name = Path.GetFileName(promoted);
    var dest = Path.Combine(outDir, name);
    if (Directory.Exists(promoted)) Directory.Move(promoted, dest);
    else File.Move(promoted, dest, overwrite: true);
}
Directory.Delete(stagingDir, recursive: true);

if (!File.Exists(ktxPath))
{
    Console.Error.WriteLine($"ktx not found at '{ktxPath}' after extraction. Archive layout may have changed.");
    return 1;
}

if (!OperatingSystem.IsWindows())
{
    try
    {
        File.SetUnixFileMode(ktxPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Warning: could not chmod +x ktx: {ex.Message}");
    }
}

await File.WriteAllTextAsync(markerPath, expectedSha);
Console.WriteLine($"Installed ktx at {ktxPath}");
return 0;
