using System.Collections.Concurrent;
using System.Security.Cryptography;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>The files a model comes from — <c>.glb</c>, <c>.blend</c>, <c>.fbx</c> — and the GLB bytes the pipeline reads for each.</summary>
/// <remarks>
/// <para>
/// A <c>.glb</c> is read as it is. A <c>.blend</c> or <c>.fbx</c> is read through the GLB headless
/// Blender converts it to (<see cref="BlenderModelConverter"/>), kept at
/// <see cref="ConvertedPath"/> and reused while its stamp still matches. Everything past this seam
/// — extraction, the mesh, skeleton and clip cooks, verify — sees one format.
/// </para>
/// <para>
/// The source's bytes are read through the caller's file system so a build records them as its
/// input. The converted GLB is derived data under <c>.editor/</c>, which a build's observed file
/// system may not write, so it is read and written on the host. A file system with no host paths
/// (a memory mount) converts in a temporary directory and persists nothing.
/// </para>
/// </remarks>
public static partial class ModelSource
{
    private static readonly ConcurrentDictionary<string, Conversion> s_latest = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, object> s_gates = new(StringComparer.Ordinal);

    /// <summary>What one source's bytes last converted to in this process: the GLB, or the failure Blender reported for exactly those bytes, so a broken source is not re-run for every document that names it.</summary>
    private sealed record Conversion(string SourceSha256, byte[]? Glb, string? Failure);

    private readonly record struct HostPaths(string Source, string Glb);

    public static bool IsModel(UPath path) => HasExtension(path, ".glb") || IsConverted(path);

    /// <summary>Whether the pipeline reads this model through a converted GLB, and so must never write into it.</summary>
    public static bool IsConverted(UPath path) => HasExtension(path, ".blend") || HasExtension(path, ".fbx");

    /// <summary>Where the GLB converted from <paramref name="source"/> lives: <c>.editor/converted/&lt;assets-relative source&gt;.glb</c>.</summary>
    public static UPath ConvertedPath(AssetProjectLayout layout, UPath source)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!source.IsInDirectory(layout.Assets, recursive: true)) throw new ArgumentException($"'{source}' is not under {layout.Assets}", nameof(source));
        return layout.EditorConverted / (source.FullName[(layout.Assets.FullName.Length + 1)..] + ".glb");
    }

    /// <summary>The model's GLB bytes: a <c>.glb</c> itself, or the current conversion of a <c>.blend</c>/<c>.fbx</c>, converting when the stored one is stale or missing.</summary>
    /// <exception cref="InvalidDataException">The source needs converting and Blender is missing, or the conversion failed.</exception>
    public static byte[] ReadGlb(IFileSystem fileSystem, UPath source, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        var bytes = fileSystem.ReadAllBytes(source);
        return IsConverted(source) ? Converted(fileSystem, source, bytes, logger ?? NullLogger.Instance) : bytes;
    }

    private static byte[] Converted(IFileSystem fileSystem, UPath source, byte[] bytes, ILogger log)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var host = HostPathsOf(fileSystem, source);
        var key = host?.Glb ?? source.FullName;

        lock (s_gates.GetOrAdd(key, static _ => new object()))
        {
            if (s_latest.TryGetValue(key, out var latest) && latest.SourceSha256 == sha)
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
                if (BlenderModelConverter.IsCurrent(previous, sha, version)) return Remember(key, new Conversion(sha, previous, null));
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
            var stamp = new BlenderModelConverter.SourceStamp(sha, BlenderModelConverter.ConverterVersion, version);
            byte[] glb;
            try
            {
                glb = host is { } paths
                    ? BlenderModelConverter.Convert(blender, paths.Source, stamp)
                    : ConvertCopy(blender, source, bytes, stamp);
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

    private static byte[] ConvertCopy(string blender, UPath source, byte[] bytes, BlenderModelConverter.SourceStamp stamp)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ParadiseModelSource", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var copy = Path.Combine(directory, source.GetName());
            File.WriteAllBytes(copy, bytes);
            return BlenderModelConverter.Convert(blender, copy, stamp);
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

    private static bool HasExtension(UPath path, string extension)
        => string.Equals(path.GetExtensionWithDot(), extension, StringComparison.OrdinalIgnoreCase);

    [LoggerMessage(EventId = 40, Level = LogLevel.Information, Message = "convert: {Source} to GLB with {Blender}")]
    private static partial void LogConverting(ILogger logger, UPath source, string blender);
}
