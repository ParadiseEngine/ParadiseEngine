// Console app built by tools/ktx/KtxBootstrap.csproj — the KTX-Software twin of
// tools/slang/SlangBootstrap.cs (same download → SHA256-verify → extract → marker shape, same
// cross-process lock; see that file for the history behind each of those decisions). Resolves a
// `ktx` CLI archive from tools/ktx/ktx.manifest.json for a given RID and installs it under the
// cache directory the caller names — typically third_party/tools/KTX-Software, which
// KtxCreate.FindKtx already probes.
//
// The manifest only carries the RIDs Khronos ships an EXTRACTABLE archive for (the Linux
// tarballs — which is what CI runs). Windows and macOS releases are installers (NSIS .exe /
// .pkg) that no supported tool unpacks reliably, so on those RIDs this exits with guidance
// instead: install KTX-Software and set PARADISE_KTX_PATH.
//
// Args (positional):
//   --manifest <path>   tools/ktx/ktx.manifest.json
//   --rid <rid>         e.g. linux-x64
//   --out <dir>         destination cache directory (parent of bin/ktx)
//
// Exit codes: 0 = success / already-installed, 1 = failure (SHA mismatch, missing RID, network).

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

string? manifestPath = null;
string? rid = null;
string? outDir = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--manifest" when i + 1 < args.Length: manifestPath = args[++i]; break;
        case "--rid" when i + 1 < args.Length: rid = args[++i]; break;
        case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
    }
}

if (manifestPath is null || rid is null || outDir is null)
{
    Console.Error.WriteLine("Usage: KtxBootstrap --manifest <path> --rid <rid> --out <dir>");
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
else
{
    Console.Error.WriteLine($"Unsupported KTX archive format '{format}' (expected 'zip', 'tar.gz' or 'tar.bz2').");
    return 1;
}

try { File.Delete(archivePath); } catch { }

// KTX tarballs unpack into a single top-level directory (KTX-Software-4.4.2-Linux-x86_64/);
// promote its contents so <out>/bin/ktx resolves uniformly.
var stagedEntries = Directory.GetFileSystemEntries(stagingDir);
string promoteRoot = stagingDir;
if (stagedEntries.Length == 1 && Directory.Exists(stagedEntries[0]))
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
