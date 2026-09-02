using System.Security.Cryptography;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>The outcome of one build.</summary>
public sealed record BuildResult(bool Succeeded, IReadOnlyList<string> Errors, int AssetCount, UPath Output);

/// <summary>The <c>build</c> verb: compiles <c>assets/</c> into a build-shaped output tree.</summary>
/// <remarks>
/// No lookup table of asset kinds on purpose: what an asset IS lives in the importers, so a
/// project can append one that shadows a built-in without this file changing (library-only
/// today; the CLI cannot pass a chain — issue #208). A successful build's tree holds exactly what
/// it produced: the manifest goes first and comes back last, so a tree without one is a tree a
/// build did not finish, and whatever the build did not write is swept before the manifest
/// returns (issues #201, #202).
/// </remarks>
public sealed class BuildRunner
{
    private readonly IFileSystem _fileSystem;
    private readonly AssetProjectLayout _layout;
    private readonly ITextureEncoder? _encoder;
    private readonly Action<string>? _log;
    private readonly Action<string>? _warn;
    private readonly IReadOnlyList<IAssetImporter> _importers;

    public BuildRunner(
        IFileSystem fileSystem,
        AssetProjectLayout layout,
        ITextureEncoder? encoder,
        Action<string>? log = null,
        Action<string>? warn = null,
        IReadOnlyList<IAssetImporter>? importers = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);

        _fileSystem = fileSystem;
        _layout = layout;
        _encoder = encoder;
        _log = log;
        _warn = warn;
        _importers = importers ?? AssetImporters.All;
    }

    /// <summary>Builds the named profile, or the defaults for null; this must NOT bless a name like <c>dev</c>, or the CLI can silently fall out of step with it.</summary>
    /// <remarks>Never throws for a bad tree: watch runs this in a loop, and a build that took the process down with it reports nothing (issue #203).</remarks>
    public BuildResult Run(string? profileName = null, ProjectOutputTarget target = ProjectOutputTarget.Build)
    {
        var output = _layout.OutputFor(target);
        try
        {
            return RunCore(profileName, target, output);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SidecarMetaException)
        {
            return new BuildResult(false, [$"build aborted: {error.Message}"], 0, output);
        }
    }

    private BuildResult RunCore(string? profileName, ProjectOutputTarget target, UPath output)
    {
        var errors = new List<string>();

        ProjectManifest projectManifest;
        try
        {
            projectManifest = ProjectManifest.Load(_fileSystem, _layout.Manifest);
        }
        catch (ProjectManifestException failure)
        {
            return new BuildResult(false, [failure.Message], 0, output);
        }

        BuildProfile? profile = BuildProfile.Default;
        if (profileName is not null && !projectManifest.TryGetProfile(profileName, out profile))
        {
            return new BuildResult(
                false,
                [$"project.toml declares no build profile '{profileName}' (declared: {string.Join(", ", projectManifest.Profiles.Keys.DefaultIfEmpty("none"))})"],
                0, output);
        }

        var sources = AssetPaths.Scan(_fileSystem, _layout.Assets);
        var findings = ProjectVerifier.Verify(_fileSystem, _layout, sources);
        var verifyErrors = findings.Where(finding => finding.Severity == VerifySeverity.Error).ToList();
        if (verifyErrors.Count > 0)
        {
            return new BuildResult(
                false,
                verifyErrors.Select(finding => finding.ToString()).ToList(),
                0, output);
        }

        var cache = ArtifactCache.ForProject(_fileSystem, _layout, _warn);
        var manifest = new BuildManifest { Project = projectManifest.Name, Profile = profileName ?? "" };
        if (!_fileSystem.DirectoryExists(output)) _fileSystem.CreateDirectory(output);

        // From here until Save the tree is in flux, and a manifest describing the previous one
        // would be believed by whoever reads it (#202).
        if (_fileSystem.FileExists(output / BuildManifest.FileName)) _fileSystem.DeleteFile(output / BuildManifest.FileName);

        var index = BuildIndex.Load(_fileSystem, output, profileName, target, Environment());
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var path in sources.Files)
        {
            // The manifest is the built tree's identity database; copying sidecars was a second
            // copy of the same facts.
            if (SidecarMeta.IsSidecarPath(path) || AssetClassifier.IsJunk(path)) continue;

            var relative = sources.Relative(path);
            try
            {
                if (index.TryReuse(_fileSystem, sources, relative, output, out var already))
                {
                    Claim(owners, already, errors);
                    manifest.Assets.AddRange(already);
                    continue;
                }

                var produced = manifest.Assets.Count;
                var before = errors.Count;
                var (handler, inputs) = Offer(path, relative, profile!, target, cache, output, manifest, sources, errors);
                var written = manifest.Assets[produced..];
                Claim(owners, written, errors);

                if (handler is not null && errors.Count == before)
                {
                    index.Record(relative, inputs, written);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Watch runs this loop unattended; an importer that throws must cost one asset,
                // not the process — and must say which asset (#203).
                errors.Add($"{relative}: {error.GetType().Name}: {error.Message}");
            }
        }

        if (errors.Count > 0) return new BuildResult(false, errors, manifest.Assets.Count, output);

        Sweep(output, owners.Keys);

        // An index saved beside a half-failed tree would be trusted by the next run (#202).
        index.Save(_fileSystem, output);

        manifest.Save(_fileSystem, output / BuildManifest.FileName);
        return new BuildResult(true, [], manifest.Assets.Count, output);
    }

    /// <summary>What shapes output without being read from <c>assets/</c> by an importer: the encoder and the manifest (its profile table reaches every importer as <see cref="BuildProfile"/>).</summary>
    private string Environment()
    {
        var manifest = Convert.ToHexStringLower(SHA256.HashData(_fileSystem.ReadAllBytes(_layout.Manifest)));
        return $"encoder={_encoder?.Identity ?? ""};manifest={manifest}";
    }

    private (IAssetImporter? Handler, IReadOnlyList<BuildInput> Inputs) Offer(
        UPath path,
        string relative,
        BuildProfile profile,
        ProjectOutputTarget target,
        ArtifactCache cache,
        UPath output,
        BuildManifest manifest,
        AssetPaths sources,
        List<string> errors)
    {
        using var observed = new ObservedSources(_fileSystem, sources);
        var meta = SidecarMeta.Load(observed, SidecarMeta.PathFor(path));

        using var written = new RecordingFileSystem(_fileSystem, output);
        var context = new ImportContext(
            observed, sources, path, relative, meta,
            profile, target, written, cache, _encoder, _log);

        IAssetImporter? handler = null;
        for (var i = _importers.Count - 1; i >= 0 && handler is null; i--)
        {
            if (_importers[i].Import(context, errors)) handler = _importers[i];
        }

        if (handler is null) return (null, observed.Records);

        foreach (var file in written.Written)
        {
            var bytes = written.ReadAllBytes(file);
            manifest.Assets.Add(new BuiltAsset
            {
                Path = file.FullName[1..],
                Source = context.Source,
                Guid = handler.RecordsIdentity && meta is { } identified ? DocumentGuid.Format(identified.Guid) : null,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                Size = bytes.Length,
            });
        }

        return (handler, observed.Records);
    }

    /// <summary>Two sources landing on one output path is last-writer-wins and a manifest with two entries for one file (#202).</summary>
    private static void Claim(Dictionary<string, string> owners, IReadOnlyList<BuiltAsset> assets, List<string> errors)
    {
        foreach (var asset in assets)
        {
            if (owners.TryGetValue(asset.Path, out var other) && other != asset.Source)
            {
                errors.Add($"{asset.Source}: builds to '{asset.Path}', which '{other}' also builds to; one of them must be renamed");
                continue;
            }

            owners[asset.Path] = asset.Source;
        }
    }

    /// <summary>Removes what this build did not produce: outputs of deleted sources, outputs under a retired naming policy, partial files from a killed build (#201).</summary>
    private void Sweep(UPath output, IEnumerable<string> produced)
    {
        var keep = new HashSet<string>(produced, StringComparer.Ordinal) { BuildIndex.FileName, BuildManifest.FileName };

        foreach (var file in _fileSystem.EnumerateFiles(output, "*", SearchOption.AllDirectories).ToList())
        {
            var relative = file.FullName[(output.FullName.Length + 1)..];
            if (keep.Contains(relative)) continue;

            try
            {
                _fileSystem.DeleteFile(file);
                _log?.Invoke($"swept: {relative}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _warn?.Invoke($"could not sweep stale output '{relative}' ({error.Message})");
            }
        }

        foreach (var directory in _fileSystem.EnumerateDirectories(output, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.FullName.Length)
            .ToList())
        {
            try
            {
                if (!_fileSystem.EnumeratePaths(directory).Any()) _fileSystem.DeleteDirectory(directory, isRecursive: false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
