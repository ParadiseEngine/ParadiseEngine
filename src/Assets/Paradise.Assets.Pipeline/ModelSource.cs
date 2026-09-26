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
/// and rebuilds when one changes. The converted GLB is derived data under <c>.editor/</c>, which a
/// build's observed file system may not write, so it is read and written on the host. A file
/// system with no host paths (a memory mount) converts in a temporary directory and persists nothing.
/// </para>
/// </remarks>
public static partial class ModelSource
{
    private static readonly ConcurrentDictionary<string, Conversion> s_latest = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, object> s_gates = new(StringComparer.Ordinal);

    /// <summary>What one source's bytes last converted to in this process: the GLB, or the failure Blender reported for exactly those bytes, so a broken source is not re-run for every document that names it.</summary>
    private sealed record Conversion(string SourceSha256, byte[]? Glb, string? Failure);

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
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var host = HostPathsOf(fileSystem, source);
        var key = host?.Glb ?? source.FullName;
        string? Dependency(string relative) => DependencySha256(fileSystem, source, relative);

        lock (s_gates.GetOrAdd(key, static _ => new object()))
        {
            // Checked on every read, not just the first: the check is what reads each dependency
            // through the caller's file system, so a build records it however warm this process is.
            if (s_latest.TryGetValue(key, out var latest) && latest.SourceSha256 == sha
                && (latest.Glb is null || BlenderModelConverter.IsCurrent(latest.Glb, sha, null, Dependency)))
            {
                if (latest.Failure is not null) throw new InvalidDataException(latest.Failure);
                if (host is { } kept && !File.Exists(kept.Glb)) Persist(kept.Glb, latest.Glb!);
                return latest.Glb!;
            }

            var blender = BlenderModelConverter.FindBlender();
            var version = blender is null ? null : BlenderModelConverter.BlenderVersion(blender);
            if (host is { } stored && File.Exists(stored.Glb))
            {
                var previous = File.ReadAllBytes(stored.Glb);
                if (BlenderModelConverter.IsCurrent(previous, sha, version, Dependency)) return Remember(key, new Conversion(sha, previous, null));
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

            LogConverting(log, source, blender);
            byte[] glb;
            try
            {
                var export = host is { } paths
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
                Remember(key, new Conversion(sha, null, failure.Message));
                throw;
            }

            if (host is { } target) Persist(target.Glb, glb);
            return Remember(key, new Conversion(sha, glb, null));
        }
    }

    /// <summary>
    /// The SHA-256 of a file a conversion read, named relative to the source's directory (absolute
    /// only across Windows drives); null when it is gone. Read through <paramref name="fileSystem"/>
    /// wherever it lies, so under <c>assets/</c> a build's observed file system records it as an
    /// input (a presence miss included), and outside it the host answers.
    /// </summary>
    private static string? DependencySha256(IFileSystem fileSystem, UPath source, string relative)
    {
        try
        {
            var path = Path.IsPathRooted(relative)
                ? fileSystem.ConvertPathFromInternal(relative)
                : (source.GetDirectory() / relative).ToAbsolute();
            return fileSystem.FileExists(path) ? Convert.ToHexStringLower(SHA256.HashData(fileSystem.ReadAllBytes(path))) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static byte[] Remember(string key, Conversion conversion)
    {
        s_latest[key] = conversion;
        return conversion.Glb!;
    }

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
