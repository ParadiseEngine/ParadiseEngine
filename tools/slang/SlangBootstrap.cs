// Console app built by tools/slang/SlangBootstrap.csproj and invoked from src/Slang.targets.
// Resolves a slangc archive from tools/slang/slang.manifest.json for a given RID, verifies SHA256
// against the manifest (hard fail on mismatch — supply chain trust anchor), extracts to the cache
// directory passed by the caller, and writes a marker file so the second build is a no-op.
//
// Args (positional):
//   --manifest <path>   tools/slang/slang.manifest.json
//   --rid <rid>         e.g. linux-x64
//   --out <dir>         destination cache directory (parent of bin/slangc)
//
// Exit codes: 0 = success / already-installed, 1 = failure (SHA mismatch, missing RID, network).
//
// History: originally implemented as a `dotnet run --file SlangBootstrap.cs` script. CI's parallel
// project scheduler raced on the dotnet-runfile shared cache (~/.local/share/dotnet/runfile/...)
// when two consumer projects invoked RestoreSlang concurrently — surfaced as MSB3491 on
// AssemblyInfoInputs.cache. Promoted to a real csproj so the build artifacts live in a
// project-scoped obj/bin under tools/slang/, which MSBuild serializes naturally.

using System.Formats.Tar;
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
    Console.Error.WriteLine("Usage: SlangBootstrap.cs --manifest <path> --rid <rid> --out <dir>");
    return 1;
}

using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
var root = doc.RootElement;
if (!root.GetProperty("rids").TryGetProperty(rid, out var entry))
{
    Console.Error.WriteLine($"Slang manifest at '{manifestPath}' has no entry for RID '{rid}'.");
    return 1;
}

var url = entry.GetProperty("url").GetString()!;
var expectedSha = entry.GetProperty("sha256").GetString()!;
var format = entry.GetProperty("format").GetString()!;

Directory.CreateDirectory(outDir);
var markerPath = Path.Combine(outDir, ".installed");
var slangcName = OperatingSystem.IsWindows() ? "slangc.exe" : "slangc";
var slangcPath = Path.Combine(outDir, "bin", slangcName);

// Cross-process lock — two MSBuild Exec calls (e.g. Sample + WebGPU.Test invoking RestoreSlang
// in parallel) will both reach this binary at once, both attempt to download `slang-archive.tar.gz`
// to the same path, and one will throw IOException ("being used by another process"). Hold a
// FileShare.None handle on a sentinel file in the cache root so the second invocation blocks
// until the first completes; the second's marker check then short-circuits the work.
var lockDir = Path.GetDirectoryName(outDir) ?? outDir;
Directory.CreateDirectory(lockDir);
var lockPath = Path.Combine(lockDir, ".bootstrap.lock");
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
            Console.Error.WriteLine($"Timed out waiting on Slang bootstrap lock at '{lockPath}'.");
            return 1;
        }
        await Task.Delay(250);
    }
}
using var _lock = lockHandle;

if (File.Exists(markerPath) && File.Exists(slangcPath))
{
    var existing = File.ReadAllText(markerPath).Trim();
    if (string.Equals(existing, expectedSha, StringComparison.OrdinalIgnoreCase))
    {
        // Already installed at the requested SHA — no-op.
        return 0;
    }
}

Console.WriteLine($"Downloading slangc from {url}");
var archivePath = Path.Combine(outDir, "slang-archive." + format);
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
    Console.Error.WriteLine($"Slang archive SHA256 mismatch: expected '{expectedSha}', got '{actualSha}' (source: {url}).");
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
else if (string.Equals(format, "tar.gz", StringComparison.OrdinalIgnoreCase))
{
    await using var fs = File.OpenRead(archivePath);
    await using var gz = new GZipStream(fs, CompressionMode.Decompress);
    await TarFile.ExtractToDirectoryAsync(gz, stagingDir, overwriteFiles: true);
}
else
{
    Console.Error.WriteLine($"Unsupported Slang archive format '{format}' (expected 'zip' or 'tar.gz').");
    return 1;
}

try { File.Delete(archivePath); } catch { }

// Many slang archives unpack into a single top-level directory (e.g. slang-2026.7-linux-x86_64/).
// Promote that directory's contents up one level so $(SlangDir)/bin/slangc resolves uniformly
// regardless of the archive's internal layout.
var stagedEntries = Directory.GetFileSystemEntries(stagingDir);
string promoteRoot = stagingDir;
if (stagedEntries.Length == 1 && Directory.Exists(stagedEntries[0]))
{
    promoteRoot = stagedEntries[0];
}

// Clear destination contents but keep the directory itself (it may be the marker root).
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

if (!File.Exists(slangcPath))
{
    Console.Error.WriteLine($"slangc not found at '{slangcPath}' after extraction. Archive layout may have changed.");
    return 1;
}

if (!OperatingSystem.IsWindows())
{
    try
    {
        File.SetUnixFileMode(slangcPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Warning: could not chmod +x slangc: {ex.Message}");
    }
}

await File.WriteAllTextAsync(markerPath, expectedSha);
Console.WriteLine($"Installed slangc at {slangcPath}");
return 0;
