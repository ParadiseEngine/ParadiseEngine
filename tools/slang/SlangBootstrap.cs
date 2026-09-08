// Installs the manifest's RID-specific Slang archive after SHA256 verification.
// Usage: --manifest <path> --rid <rid> --out <cache directory>; exits 0 on success, 1 on failure.
// The .installed marker skips matching installations. Keep this a csproj: parallel consumers
// race on dotnet run-file caches, whereas MSBuild coordinates project outputs.

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

// Serialize concurrent MSBuild installations with a FileShare.None cache lock;
// the next invocation checks the completed installation's marker.
var lockDir = Path.GetDirectoryName(outDir) ?? outDir;
Directory.CreateDirectory(lockDir);
var lockPath = Path.Combine(lockDir, ".bootstrap.lock");
FileStream lockHandle;
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
await using (var fs = File.OpenRead(archivePath))
{
    actualSha = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs));
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
    // Use system tar: Apple's archives contain binary PAX extended attributes that
    // System.Formats.Tar rejects. Windows archives use the ZIP branch.
    using var tar = Process.Start(new ProcessStartInfo("tar")
    {
        ArgumentList = { "-xzf", archivePath, "-C", stagingDir },
        UseShellExecute = false,
        RedirectStandardError = true,
    });
    if (tar is null)
    {
        Console.Error.WriteLine("Failed to start 'tar' to extract the Slang archive.");
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
