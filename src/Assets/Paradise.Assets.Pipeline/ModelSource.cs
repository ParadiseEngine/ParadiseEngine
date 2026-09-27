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
/// A <c>.blend</c> with collections marked as assets is several models, one per collection and
/// named by it (<see cref="Assets"/>): one conversion exports all of them, each to its own GLB,
/// and a read names the one it wants. Without asset collections it is one model, as every other
/// source is.
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
    /// stamped models when nothing persisted them (a file system with no host paths).
    /// </summary>
    /// <remarks>
    /// A failure stands only while the files the failed import read still hash as they did
    /// (<paramref name="FailureInputs"/>), so fixing a <c>.mtl</c> or texture under a running watch
    /// converts again. When Blender failed before listing what it read — it could not open a
    /// <c>.blend</c>, or crashed — or what it wrote could not be read, there are none, and only a
    /// new source or Blender retries.
    /// </remarks>
    private sealed record Conversion(string SourceSha256, string? BlenderVersion, IReadOnlyList<BlenderModelConverter.ExportedModel>? Held, string? Failure, IReadOnlyList<FailureInput> FailureInputs);

    /// <summary>A file a failed conversion read, relative to the source's directory, and its SHA-256 then (null when it was already gone).</summary>
    private readonly record struct FailureInput(string Path, string? Sha256);

    /// <summary>The source, its whole-file GLB and the directory of its per-asset GLBs, on the host.</summary>
    private readonly record struct HostPaths(string Source, string Glb, string AssetsDirectory);

    /// <summary>What a current conversion says of a source: every asset it holds (none for a one-model source), and the GLB asked for, or null when the source has no such model.</summary>
    private readonly record struct Model(IReadOnlyList<string> Assets, byte[]? Glb);

    public static bool IsModel(UPath path) => IsDirect(path) || IsConverted(path);

    /// <summary>Whether the pipeline reads this model through a converted GLB, and so must never write into it.</summary>
    public static bool IsConverted(UPath path)
    {
        var extension = path.GetExtensionWithDot();
        return extension is not null && BlenderModelConverter.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every extension <see cref="IsModel"/> accepts, <c>.glb</c> and <c>.gltf</c> first, lowercase with the dot.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".glb", ".gltf", .. BlenderModelConverter.Extensions];

    /// <summary>Whether a source of this kind can hold asset collections, each its own model: a <c>.blend</c>.</summary>
    public static bool CanHoldAssets(UPath path) => HasExtension(path, ".blend");

    /// <summary>Where the GLB converted from <paramref name="source"/> lives: <c>.editor/converted/&lt;assets-relative source&gt;.glb</c>, or for one of its <paramref name="asset"/>s <c>.editor/converted/&lt;assets-relative source&gt;/&lt;asset&gt;.glb</c>.</summary>
    public static UPath ConvertedPath(AssetProjectLayout layout, UPath source, string? asset = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!source.IsInDirectory(layout.Assets, recursive: true)) throw new ArgumentException($"'{source}' is not under {layout.Assets}", nameof(source));
        var relative = source.FullName[(layout.Assets.FullName.Length + 1)..];
        return asset is null ? layout.EditorConverted / (relative + ".glb") : layout.EditorConverted / relative / (asset + ".glb");
    }

    /// <summary>The directory holding the per-asset GLBs of <paramref name="source"/>: <c>.editor/converted/&lt;assets-relative source&gt;</c>.</summary>
    public static UPath ConvertedDirectory(AssetProjectLayout layout, UPath source)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!source.IsInDirectory(layout.Assets, recursive: true)) throw new ArgumentException($"'{source}' is not under {layout.Assets}", nameof(source));
        return layout.EditorConverted / source.FullName[(layout.Assets.FullName.Length + 1)..];
    }

    /// <summary>
    /// The asset names of a source that holds several models, in ordinal order; empty for a source
    /// that is one model, which every source but a <c>.blend</c> with asset collections is. Answered
    /// from the current conversion, converting when it is stale or missing.
    /// </summary>
    /// <exception cref="InvalidDataException">The source needs converting and Blender is missing, or the conversion failed.</exception>
    public static IReadOnlyList<string> Assets(IFileSystem fileSystem, UPath source, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (!CanHoldAssets(source)) return [];
        return Converted(fileSystem, source, fileSystem.ReadAllBytes(source), logger ?? NullLogger.Instance, asset: null).Assets;
    }

    /// <summary>
    /// The model's GLB bytes: a <c>.glb</c> itself, a <c>.gltf</c> with its buffers, or the current
    /// conversion of any other model source, converting when the stored one is stale or missing.
    /// <paramref name="asset"/> names one model of a source with asset collections, and must be null
    /// for any other.
    /// </summary>
    /// <exception cref="InvalidDataException">A <c>.gltf</c> or one of its buffers cannot be read, or the source needs converting and Blender is missing, or the conversion failed, or the source holds no model <paramref name="asset"/> names.</exception>
    public static byte[] ReadGlb(IFileSystem fileSystem, UPath source, ILogger? logger = null, string? asset = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (asset is not null && !CanHoldAssets(source)) throw new InvalidDataException(AssetProblem([], asset));
        if (GltfFile.Is(source)) return GltfFile.ReadGlb(fileSystem, source);

        var bytes = fileSystem.ReadAllBytes(source);
        if (!IsConverted(source)) return bytes;

        var model = Converted(fileSystem, source, bytes, logger ?? NullLogger.Instance, asset);
        return model.Glb ?? throw new InvalidDataException(AssetProblem(model.Assets, asset));
    }

    /// <summary>Why a source holding <paramref name="assets"/> has no model <paramref name="asset"/> names (null: the whole source); null when it has one.</summary>
    public static string? AssetProblem(IReadOnlyList<string> assets, string? asset)
    {
        ArgumentNullException.ThrowIfNull(assets);
        if (asset is null)
        {
            return assets.Count == 0
                ? null
                : $"holds asset collections {Quoted(assets)}, one model each, so it is no one model; a document names its model with asset = \"<name>\"";
        }

        if (assets.Contains(asset, StringComparer.Ordinal)) return null;
        return assets.Count == 0
            ? $"has no asset collection '{asset}': it holds none, so all of it is one model"
            : $"has no asset collection '{asset}' (it holds {Quoted(assets)}); a renamed or removed collection leaves the documents that named it behind";
    }

    private static string Quoted(IReadOnlyList<string> names) => string.Join(", ", names.Select(name => $"'{name}'"));

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

    private static Model Converted(IFileSystem fileSystem, UPath source, byte[] bytes, ILogger log, string? asset)
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
            if (Stored(host, key, sha, version, Dependency, asset) is { } previous) return previous;
            if (s_latest.TryGetValue(key, out var latest) && latest.Failure is not null
                && latest.SourceSha256 == sha && latest.BlenderVersion == version
                && latest.FailureInputs.All(input => Dependency(input.Path) == input.Sha256))
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

                BlenderModelConverter.Export export;
                IReadOnlyList<BlenderModelConverter.ExportedModel>? models = null;
                try
                {
                    export = host is { } paths
                        ? BlenderModelConverter.Convert(blender, paths.Source)
                        : ConvertCopy(blender, source, bytes);

                    if (export.Models is not null) models = Stamped(export, sha, version, Dependency);
                }
                catch (InvalidDataException failure)
                {
                    s_latest[key] = new Conversion(sha, version, null, failure.Message, []);
                    throw;
                }

                // Blender read the source and its dependencies at moments of its own: one saved
                // meanwhile may be in the export while the stamp names other bytes, or the reverse,
                // and a failure recorded against the saved bytes would stand for bytes it never read.
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

                if (models is null)
                {
                    FailureInput[] inputs = [.. export.Dependencies.Select(relative => new FailureInput(relative, Dependency(relative)))];
                    s_latest[key] = new Conversion(sha, version, null, export.Failure, inputs);
                    throw new InvalidDataException(export.Failure);
                }

                if (host is { } target) Persist(target, models);

                // A persisted GLB is its own cache; only what nothing persists is held.
                s_latest[key] = new Conversion(sha, version, host is null ? models : null, null, []);
                return Select(models, asset);
            }
        }
    }

    /// <summary>Each exported model stamped with what it was made from: the source's hash, the converter and Blender, every dependency that still hashes, and for an asset its name and all of its siblings'.</summary>
    private static List<BlenderModelConverter.ExportedModel> Stamped(BlenderModelConverter.Export export, string sha, string blenderVersion, Func<string, string?> dependencySha256)
    {
        var dependencies = new List<BlenderModelConverter.Dependency>();
        foreach (var relative in export.Dependencies)
        {
            if (dependencySha256(relative) is { } dependencySha) dependencies.Add(new BlenderModelConverter.Dependency(relative, dependencySha));
        }

        var assets = export.Models!.Select(model => model.Asset).OfType<string>().ToList();
        return [.. export.Models!.Select(model => model with
        {
            Glb = BlenderModelConverter.Stamp(model.Glb, new BlenderModelConverter.SourceStamp(
                sha, BlenderModelConverter.ConverterVersion, blenderVersion, dependencies, model.Asset, model.Asset is null ? null : assets)),
        })];
    }

    private static Model Select(IReadOnlyList<BlenderModelConverter.ExportedModel> models, string? asset)
        => new([.. models.Select(model => model.Asset).OfType<string>()], models.FirstOrDefault(model => model.Asset == asset).Glb);

    /// <summary>
    /// What the conversion last made for this source says, when it is still current: the persisted
    /// GLBs, or the models held for a source with no host paths; null when there is none to reuse.
    /// </summary>
    /// <remarks>
    /// Every GLB of one conversion carries the same stamp and names every asset, so one current GLB
    /// answers which models the source holds; the one asked for must be current itself, or the
    /// conversion is incomplete and runs again.
    /// </remarks>
    private static Model? Stored(HostPaths? host, string key, string sha, string? version, Func<string, string?> dependency, string? asset)
    {
        if (host is not { } paths)
        {
            if (!s_latest.TryGetValue(key, out var latest) || latest.SourceSha256 != sha || latest.Held is not { Count: > 0 } held) return null;
            return held.All(model => BlenderModelConverter.IsCurrent(model.Glb, sha, version, dependency, model.Asset)) ? Select(held, asset) : null;
        }

        if (asset is not null && ReadHost(Path.Combine(paths.AssetsDirectory, asset + ".glb")) is { } own
            && BlenderModelConverter.IsCurrent(own, sha, version, dependency, asset)
            && BlenderModelConverter.StampedAssets(own) is { } named && named.Contains(asset, StringComparer.Ordinal))
        {
            return new Model(named, own);
        }

        if (ReadHost(paths.Glb) is { } whole && BlenderModelConverter.IsCurrent(whole, sha, version, dependency))
        {
            return new Model([], asset is null ? whole : null);
        }

        foreach (var path in GlbsIn(paths.AssetsDirectory))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (ReadHost(path) is { } bytes && BlenderModelConverter.IsCurrent(bytes, sha, version, dependency, name)
                && BlenderModelConverter.StampedAssets(bytes) is { } listed && listed.Contains(name, StringComparer.Ordinal))
            {
                return asset is not null && listed.Contains(asset, StringComparer.Ordinal) ? null : new Model(listed, null);
            }
        }

        return null;
    }

    /// <summary>A host file's bytes; null when it cannot be read.</summary>
    private static byte[]? ReadHost(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Absent, taken away between a check and the read (a clean of .editor/), or unreadable
            // (a directory at its path, or access denied): either way there is nothing to reuse.
            return null;
        }
    }

    /// <summary>The per-asset GLBs in a directory, in ordinal order; none when it cannot be listed.</summary>
    private static List<string> GlbsIn(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? [.. Directory.EnumerateFiles(directory, "*.glb").Order(StringComparer.Ordinal)] : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
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

    /// <summary>The host paths of the source and its converted GLBs; null when the file system has none, or the source is in no project.</summary>
    private static HostPaths? HostPathsOf(IFileSystem fileSystem, UPath source)
    {
        if (!AssetProjectLayout.TryLocate(fileSystem, source.GetDirectory(), out var layout)) return null;
        if (!source.IsInDirectory(layout!.Assets, recursive: true)) return null;

        try
        {
            // A memory mount answers with its own path spelling, which names nothing on disk.
            var manifest = fileSystem.ConvertPathToInternal(layout.Manifest);
            if (!Path.IsPathRooted(manifest) || !File.Exists(manifest)) return null;
            return new HostPaths(
                fileSystem.ConvertPathToInternal(source),
                fileSystem.ConvertPathToInternal(ConvertedPath(layout, source)),
                fileSystem.ConvertPathToInternal(ConvertedDirectory(layout, source)));
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stores a conversion made elsewhere of the source now at <paramref name="source"/> — the same
    /// bytes, converted beside it under another name — as if this source's own read had made it, so
    /// the next read reuses it rather than running Blender again.
    /// </summary>
    /// <exception cref="InvalidDataException">The source has no host paths, the export failed, or the GLBs could not be written.</exception>
    internal static void Store(IFileSystem fileSystem, UPath source, BlenderModelConverter.Export export, string blenderVersion)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (HostPathsOf(fileSystem, source) is not { } host) throw new InvalidDataException($"'{source}' has no project on the host to store its conversion in");
        if (export.Models is null) throw new InvalidDataException(export.Failure ?? "the export made no GLB");

        var directory = Path.GetDirectoryName(host.Source);
        var models = Stamped(export, Sha256(fileSystem.ReadAllBytes(source)), blenderVersion, relative => DependencySha256(fileSystem, source, directory, relative));
        lock (s_gates.GetOrAdd(host.Glb, static _ => new object()))
        {
            Persist(host, models);
            s_latest.TryRemove(host.Glb, out _);
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

    /// <summary>
    /// Writes one conversion's GLBs and removes what an earlier one left that it did not make: a
    /// whole-source GLB replaces the per-asset directory, per-asset GLBs replace the whole-source one
    /// and any asset no longer in the source.
    /// </summary>
    private static void Persist(HostPaths host, IReadOnlyList<BlenderModelConverter.ExportedModel> models)
    {
        try
        {
            if (models is [{ Asset: null } whole])
            {
                Persist(host.Glb, whole.Glb);
                if (Directory.Exists(host.AssetsDirectory)) Directory.Delete(host.AssetsDirectory, recursive: true);
                return;
            }

            foreach (var model in models) Persist(Path.Combine(host.AssetsDirectory, model.Asset + ".glb"), model.Glb);

            // Ignoring case: on a disk that does, the file of an asset renamed only in case is the
            // one just written, and deleting it under its old spelling would delete that.
            var kept = models.Select(model => model.Asset + ".glb").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in GlbsIn(host.AssetsDirectory).Where(path => !kept.Contains(Path.GetFileName(path)))) File.Delete(stale);
            if (File.Exists(host.Glb)) File.Delete(host.Glb);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"the converted GLBs could not be written beside '{host.Glb}': {exception.Message}", exception);
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
