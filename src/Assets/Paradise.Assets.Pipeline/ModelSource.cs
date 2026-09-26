using System.Collections.Concurrent;
using System.Security.Cryptography;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>The files a model comes from — a <c>.glb</c>, a <c>.gltf</c>, or any format in <see cref="BlenderModelConverter.Extensions"/> — and the GLB bytes the pipeline reads for each.</summary>
/// <remarks>
/// <para>
/// A <c>.glb</c> is read as it is, and a <c>.gltf</c> as the GLB its JSON and buffers make
/// (<see cref="GltfFile"/>); both are written back in their own format. Every other model source
/// is read through the GLB headless Blender converts it to (<see cref="BlenderModelConverter"/>),
/// kept at <see cref="ConvertedPath"/> and reused while its stamp still matches. Everything past
/// this seam — extraction, the mesh, skeleton and clip cooks, verify — sees one format.
/// </para>
/// <para>
/// The source's bytes, and every file the conversion recorded as read (a texture, a <c>.mtl</c>, a
/// <c>.bin</c>), are read through the caller's file system so a build records them as its inputs
/// and rebuilds when one changes; a dependency outside its mount is only stamped
/// (<see cref="DependencySha256"/>). A conversion during which one of them was saved is discarded
/// and run again. The converted GLB is derived data under <c>.editor/</c>, which a
/// build's observed file system may not write, so it is read and written on the host. A file
/// system with no host paths (a memory mount) converts in a temporary directory and persists nothing.
/// </para>
/// </remarks>
public static partial class ModelSource
{
    private const int ConversionAttempts = 3;

    /// <summary>One entry per source, replaced by each conversion.</summary>
    private static readonly ConcurrentDictionary<string, Conversion> s_latest = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, object> s_gates = new(StringComparer.Ordinal);

    /// <summary>
    /// What one source's bytes last converted to in this process under one Blender: the failure
    /// Blender reported, so a broken source is not re-run for every document that names it, or the
    /// GLB when nothing persisted it (a file system with no host paths).
    /// </summary>
    private sealed record Conversion(string SourceSha256, string? BlenderVersion, byte[]? Glb, string? Failure);

    private readonly record struct HostPaths(string Source, string Glb);

    public static bool IsModel(UPath path) => IsDirect(path) || IsConverted(path);

    /// <summary>Whether the pipeline reads this model through a converted GLB, and so must never write into it.</summary>
    public static bool IsConverted(UPath path)
    {
        var extension = path.GetExtensionWithDot();
        return extension is not null && BlenderModelConverter.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every extension <see cref="IsModel"/> accepts, <c>.glb</c> and <c>.gltf</c> first, lowercase with the dot.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".glb", ".gltf", .. BlenderModelConverter.Extensions];

    /// <summary>Where the GLB converted from <paramref name="source"/> lives: <c>.editor/converted/&lt;assets-relative source&gt;.glb</c>.</summary>
    public static UPath ConvertedPath(AssetProjectLayout layout, UPath source)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!source.IsInDirectory(layout.Assets, recursive: true)) throw new ArgumentException($"'{source}' is not under {layout.Assets}", nameof(source));
        return layout.EditorConverted / (source.FullName[(layout.Assets.FullName.Length + 1)..] + ".glb");
    }

    /// <summary>The model's GLB bytes: a <c>.glb</c> itself, a <c>.gltf</c> with its buffers, or the current conversion of any other model source, converting when the stored one is stale or missing.</summary>
    /// <exception cref="InvalidDataException">A <c>.gltf</c> or one of its buffers cannot be read, or the source needs converting and Blender is missing, or the conversion failed.</exception>
    public static byte[] ReadGlb(IFileSystem fileSystem, UPath source, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (GltfFile.Is(source)) return GltfFile.ReadGlb(fileSystem, source);

        var bytes = fileSystem.ReadAllBytes(source);
        return IsConverted(source) ? Converted(fileSystem, source, bytes, logger ?? NullLogger.Instance) : bytes;
    }

    /// <summary>Writes <paramref name="glb"/> back into a direct model source in its own format: a <c>.glb</c> as is, a <c>.gltf</c> as its JSON and, when the bytes it holds changed, its buffer.</summary>
    /// <exception cref="InvalidDataException">The source is not direct, or a <c>.gltf</c> cannot take the rewrite (<see cref="GltfFile.WriteGlb"/>).</exception>
    public static void WriteGlb(IFileSystem fileSystem, UPath source, byte[] glb)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(glb);
        if (GltfFile.Is(source)) GltfFile.WriteGlb(fileSystem, source, glb);
        else if (HasExtension(source, ".glb")) fileSystem.WriteAllBytes(source, glb);
        else throw new InvalidDataException($"'{source.GetName()}' is read through a converted GLB and is never written");
    }

    private static byte[] Converted(IFileSystem fileSystem, UPath source, byte[] bytes, ILogger log)
    {
        var host = HostPathsOf(fileSystem, source);
        var key = host?.Glb ?? source.FullName;
        var hostDirectory = host is { } located ? Path.GetDirectoryName(located.Source) : null;
        string? Dependency(string relative) => DependencySha256(fileSystem, source, hostDirectory, relative);

        // Asked before the stored GLB is judged: a Blender upgraded under a running watch makes it stale.
        var blender = BlenderModelConverter.FindBlender();
        var version = blender is null ? null : BlenderModelConverter.BlenderVersion(blender);

        lock (s_gates.GetOrAdd(key, static _ => new object()))
        {
            var sha = Sha256(bytes);

            // Checked on every read, not just the first: the check is what reads each dependency
            // through the caller's file system, so a build records it however warm this process is.
            if (Stored(host, key, sha) is { } previous && BlenderModelConverter.IsCurrent(previous, sha, version, Dependency)) return previous;
            if (s_latest.TryGetValue(key, out var latest) && latest.Failure is not null
                && latest.SourceSha256 == sha && latest.BlenderVersion == version)
            {
                throw new InvalidDataException(latest.Failure);
            }

            if (blender is null)
            {
                throw new InvalidDataException(
                    $"converting it to GLB needs Blender, and none was found; install Blender or set {BlenderModelConverter.BlenderPathEnvironmentVariable} to its executable");
            }

            if (version is null)
            {
                throw new InvalidDataException(
                    $"Blender at '{blender}' did not answer --version; set {BlenderModelConverter.BlenderPathEnvironmentVariable} to a Blender that runs");
            }

            for (var attempt = 1; ; attempt++)
            {
                LogConverting(log, source, blender);

                var started = DateTime.UtcNow;

                byte[] glb;
                BlenderModelConverter.Export export;
                try
                {
                    export = host is { } paths
                        ? BlenderModelConverter.Convert(blender, paths.Source)
                        : ConvertCopy(blender, source, bytes);

                    var dependencies = new List<BlenderModelConverter.Dependency>();
                    foreach (var relative in export.Dependencies)
                    {
                        if (Dependency(relative) is { } dependencySha) dependencies.Add(new BlenderModelConverter.Dependency(relative, dependencySha));
                    }

                    glb = BlenderModelConverter.Stamp(export.Glb, new BlenderModelConverter.SourceStamp(sha, BlenderModelConverter.ConverterVersion, version, dependencies));
                }
                catch (InvalidDataException failure)
                {
                    s_latest[key] = new Conversion(sha, version, null, failure.Message);
                    throw;
                }

                // Blender read the source and its dependencies at moments of its own: one saved
                // meanwhile may be in the export while the stamp names other bytes, or the reverse.
                if (host is { } read && ChangedSince(read, export.Dependencies, started, sha))
                {
                    if (attempt == ConversionAttempts)
                    {
                        throw new InvalidDataException(
                            $"it or a file it reads was saved during each of {ConversionAttempts} conversions; convert it again once saving has stopped");
                    }

                    bytes = fileSystem.ReadAllBytes(source);
                    sha = Sha256(bytes);
                    continue;
                }

                if (host is { } target) Persist(target.Glb, glb);

                // A persisted GLB is its own cache; only what nothing persists is held.
                s_latest[key] = new Conversion(sha, version, host is null ? glb : null, null);
                return glb;
            }
        }
    }

    /// <summary>The converted GLB last made for this source: the persisted one, or the one held for a source with no host paths; null when there is none.</summary>
    private static byte[]? Stored(HostPaths? host, string key, string sha)
    {
        if (host is not { } paths) return s_latest.TryGetValue(key, out var latest) && latest.SourceSha256 == sha ? latest.Glb : null;

        try
        {
            return File.ReadAllBytes(paths.Glb);
        }
        catch (IOException)
        {
            // Absent, or taken away between a check and the read (a clean of .editor/): either way there is nothing to reuse.
            return null;
        }
    }

    /// <summary>Whether the source or a dependency was written or removed on the host since <paramref name="started"/>, or the source no longer hashes to <paramref name="sha"/>.</summary>
    private static bool ChangedSince(HostPaths host, IReadOnlyList<string> dependencies, DateTime started, string sha)
    {
        try
        {
            var directory = Path.GetDirectoryName(host.Source)!;
            foreach (var path in dependencies.Select(relative => Path.GetFullPath(relative, directory)).Prepend(host.Source))
            {
                // The script lists only files that exist, so one gone since was changed too.
                if (!File.Exists(path) || WrittenSince(File.GetLastWriteTimeUtc(path), started)) return true;
            }

            return Sha256(File.ReadAllBytes(host.Source)) != sha;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>A time in whole seconds may come from a file system that keeps no finer ones, which stamps a write made after <paramref name="started"/> with the second it started in.</summary>
    private static bool WrittenSince(DateTime written, DateTime started)
        => written >= started
            || (written.Ticks % TimeSpan.TicksPerSecond == 0 && written.Ticks >= started.Ticks - (started.Ticks % TimeSpan.TicksPerSecond));

    /// <summary>
    /// The SHA-256 of a file a conversion read, named relative to the source's directory (absolute
    /// only across Windows drives); null when it is gone. One <paramref name="fileSystem"/> reaches
    /// is read through it, so under <c>assets/</c> a build's observed file system records it as an
    /// input (a presence miss included). One outside the mount — another drive, or above the root
    /// of a mount rooted at the project — is read on the host from <paramref name="hostDirectory"/>:
    /// stamped and checked, so a conversion goes stale when it changes, but not a build input, so a
    /// build keeps replaying outputs made before the change until another input changes.
    /// </summary>
    private static string? DependencySha256(IFileSystem fileSystem, UPath source, string? hostDirectory, string relative)
    {
        try
        {
            if (Mounted(fileSystem, source, relative) is { } path)
            {
                return fileSystem.FileExists(path) ? Sha256(fileSystem.ReadAllBytes(path)) : null;
            }

            if (hostDirectory is null) return null;
            var hostPath = Path.GetFullPath(relative, hostDirectory);
            return File.Exists(hostPath) ? Sha256(File.ReadAllBytes(hostPath)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Where <paramref name="fileSystem"/> puts a dependency; null when it reaches no such path.</summary>
    private static UPath? Mounted(IFileSystem fileSystem, UPath source, string relative)
    {
        try
        {
            return Path.IsPathRooted(relative)
                ? fileSystem.ConvertPathFromInternal(relative)
                : (source.GetDirectory() / relative).ToAbsolute();
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The host paths of the source and its converted GLB; null when the file system has none, or the source is in no project.</summary>
    private static HostPaths? HostPathsOf(IFileSystem fileSystem, UPath source)
    {
        if (!AssetProjectLayout.TryLocate(fileSystem, source.GetDirectory(), out var layout)) return null;
        if (!source.IsInDirectory(layout!.Assets, recursive: true)) return null;

        try
        {
            // A memory mount answers with its own path spelling, which names nothing on disk.
            var manifest = fileSystem.ConvertPathToInternal(layout.Manifest);
            if (!Path.IsPathRooted(manifest) || !File.Exists(manifest)) return null;
            return new HostPaths(fileSystem.ConvertPathToInternal(source), fileSystem.ConvertPathToInternal(ConvertedPath(layout, source)));
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static BlenderModelConverter.Export ConvertCopy(string blender, UPath source, byte[] bytes)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ParadiseModelSource", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var copy = Path.Combine(directory, source.GetName());
            File.WriteAllBytes(copy, bytes);
            return BlenderModelConverter.Convert(blender, copy);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Staged beside the destination and moved over it, so a reader never sees half a GLB and a failed write leaves the previous one.</summary>
    private static void Persist(string glbPath, byte[] glb)
    {
        var staged = $"{glbPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(glbPath)!);
            File.WriteAllBytes(staged, glb);
            File.Move(staged, glbPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(staged)) File.Delete(staged);
            throw new InvalidDataException($"the converted GLB could not be written to '{glbPath}': {exception.Message}", exception);
        }
    }

    private static bool IsDirect(UPath path) => HasExtension(path, ".glb") || GltfFile.Is(path);

    private static bool HasExtension(UPath path, string extension)
        => string.Equals(path.GetExtensionWithDot(), extension, StringComparison.OrdinalIgnoreCase);

    [LoggerMessage(EventId = 40, Level = LogLevel.Information, Message = "convert: {Source} to GLB with {Blender}")]
    private static partial void LogConverting(ILogger logger, UPath source, string blender);
}
