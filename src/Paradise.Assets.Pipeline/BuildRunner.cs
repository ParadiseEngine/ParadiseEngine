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
/// today; the CLI cannot pass a chain — issue #208). Stale outputs from deleted sources are not
/// swept (issue #201); <c>clean</c> is the answer.
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
    public BuildResult Run(string? profileName = null, ProjectOutputTarget target = ProjectOutputTarget.Build)
    {
        var output = _layout.OutputFor(target);
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

        var findings = ProjectVerifier.Verify(_fileSystem, _layout);
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

        var index = BuildIndex.Load(_fileSystem, output, profileName, target);

        foreach (var path in _fileSystem.EnumerateFiles(_layout.Assets, "*", SearchOption.AllDirectories).OrderBy(p => p.FullName, StringComparer.Ordinal))
        {
            // The manifest is the built tree's identity database; copying sidecars was a second
            // copy of the same facts.
            if (SidecarMeta.IsSidecarPath(path)) continue;

            // No gate here: recording is gated on DeterministicCopy, so the rule lives once.
            if (index.TryReuse(_fileSystem, path, Relative(path), output, out var already))
            {
                manifest.Assets.AddRange(already);
                continue;
            }

            var produced = manifest.Assets.Count;

            var handler = Offer(path, profile!, target, cache, output, manifest, errors);

            if (handler is { DeterministicCopy: true } && errors.Count == 0)
            {
                index.Record(_fileSystem, path, Relative(path), manifest.Assets[produced..]);
            }
        }

        if (errors.Count > 0) return new BuildResult(false, errors, manifest.Assets.Count, output);

        // An index saved beside a half-failed tree would be trusted by the next run (#202).
        index.Save(_fileSystem, output);

        manifest.Save(_fileSystem, output / BuildManifest.FileName);
        return new BuildResult(true, [], manifest.Assets.Count, output);
    }

    private IAssetImporter? Offer(UPath path, BuildProfile profile, ProjectOutputTarget target, ArtifactCache cache, UPath output, BuildManifest manifest, List<string> errors)
    {
        var meta = SidecarMeta.IsSidecarPath(path) ? null : SidecarMeta.Load(_fileSystem, SidecarMeta.PathFor(path));

        using var observed = new RecordingFileSystem(_fileSystem, output);
        var context = new ImportContext(
            _fileSystem, _layout.Assets, path, Relative(path), meta,
            profile, target, observed, cache, _encoder, _log);

        IAssetImporter? handler = null;
        for (var i = _importers.Count - 1; i >= 0 && handler is null; i--)
        {
            if (_importers[i].Import(context, errors)) handler = _importers[i];
        }

        if (handler is null) return null;

        foreach (var written in observed.Written)
        {
            var bytes = observed.ReadAllBytes(written);
            manifest.Assets.Add(new BuiltAsset
            {
                Path = written.FullName[1..],
                Source = context.Source,
                Guid = handler.RecordsIdentity && meta is { } identified ? DocumentGuid.Format(identified.Guid) : null,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                Size = bytes.Length,
            });
        }

        return handler;
    }

    private string Relative(UPath path) => path.FullName[(_layout.Assets.FullName.Length + 1)..];
}
