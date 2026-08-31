using System.Security.Cryptography;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>The outcome of one build.</summary>
/// <param name="Succeeded">Whether the tree at <paramref name="Output"/> is complete and consistent.</param>
/// <param name="Errors">What stopped or degraded the build; empty on success.</param>
/// <param name="AssetCount">How many assets the manifest records.</param>
/// <param name="Output">The build tree that was written.</param>
public sealed record BuildResult(bool Succeeded, IReadOnlyList<string> Errors, int AssetCount, UPath Output);

/// <summary>
/// The <c>build</c> verb: compiles <c>assets/</c> into a build-shaped output tree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure orchestration.</b> The runner walks the tree, consults the reuse index, and runs the
/// one process every asset gets: find the <see cref="IAssetImporter"/> claiming the path in the
/// target's set, hand it the <see cref="ImportContext"/> — whose output is the build tree
/// mounted and observed (<see cref="RecordingFileSystem"/>) — and record what was actually
/// written. What an asset IS lives entirely inside the importers, and a pipeline FLAVOR is a
/// choice of set (<see cref="AssetImporters.For"/>); nothing here changes when either grows.
/// </para>
/// <para>
/// A build begins with <see cref="ProjectVerifier"/> and refuses on any error — the manifest
/// records each asset's GUID, so an inconsistent source tree cannot produce a consistent
/// manifest anyway. Stale outputs from deleted sources are not swept; <c>clean</c> is the
/// wholesale answer and costs only time.
/// </para>
/// </remarks>
public sealed class BuildRunner
{
    private readonly IFileSystem _fileSystem;
    private readonly AssetProjectLayout _layout;
    private readonly ITextureEncoder? _encoder;
    private readonly Action<string>? _log;
    private readonly Action<string>? _warn;

    /// <summary>Creates a runner for one project.</summary>
    /// <param name="fileSystem">The filesystem holding the project.</param>
    /// <param name="layout">The located project.</param>
    /// <param name="encoder">
    /// The texture encoder, or <see langword="null"/> when no <c>ktx</c> is available — the
    /// build then fails if (and only if) there are textures to encode.
    /// </param>
    /// <param name="log">Progress lines.</param>
    /// <param name="warn">Non-fatal trouble (also handed to the artifact cache).</param>
    public BuildRunner(IFileSystem fileSystem, AssetProjectLayout layout, ITextureEncoder? encoder, Action<string>? log = null, Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);

        _fileSystem = fileSystem;
        _layout = layout;
        _encoder = encoder;
        _log = log;
        _warn = warn;
    }

    /// <summary>Builds the named profile into <paramref name="target"/>'s tree.</summary>
    /// <param name="profileName">A profile declared in <c>project.toml</c>; <c>dev</c> falls back to defaults when undeclared.</param>
    /// <param name="target">Which build-shaped tree to write.</param>
    public BuildResult Run(string profileName, ProjectOutputTarget target = ProjectOutputTarget.Build)
    {
        ArgumentException.ThrowIfNullOrEmpty(profileName);

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

        if (!projectManifest.TryGetProfile(profileName, out var profile))
        {
            // An undeclared "dev" means the defaults: every project has an iteration loop
            // whether or not it wrote a profiles table yet.
            if (profileName != "dev")
            {
                return new BuildResult(
                    false,
                    [$"project.toml declares no build profile '{profileName}' (declared: {string.Join(", ", projectManifest.Profiles.Keys.DefaultIfEmpty("none"))})"],
                    0, output);
            }

            profile = BuildProfile.Default;
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
        var manifest = new BuildManifest { Project = projectManifest.Name, Profile = profileName };
        if (!_fileSystem.DirectoryExists(output)) _fileSystem.CreateDirectory(output);

        var index = BuildIndex.Load(_fileSystem, output, profileName, target);
        var importers = AssetImporters.For(target);

        foreach (var path in _fileSystem.EnumerateFiles(_layout.Assets, "*", SearchOption.AllDirectories).OrderBy(p => p.FullName, StringComparer.Ordinal))
        {
            var classification = AssetClassifier.Classify(_layout.Assets, path);

            // Only what the index may legitimately answer for -- see BuildIndex's remarks for why
            // textures and documents are excluded, and why excluding them is the point.
            if (Reusable(importers, classification, path)
                && index.TryReuse(_fileSystem, path, Relative(path), output, out var already))
            {
                manifest.Assets.AddRange(already);
                continue;
            }

            // What this source produced, taken as the entries the importer's writes append. Done
            // here rather than inside any importer, so none of them knows an index exists.
            var produced = manifest.Assets.Count;

            // ONE process for every asset. The runner does not know what anything IS — the
            // TARGET'S importer set decides, and the importer claiming the extension writes the
            // output mount itself. The manifest is the single exception: it configures the build
            // rather than being built by it, and Other is by definition unclaimed (and already a
            // verify warning).
            if (classification is not (AssetClass.Manifest or AssetClass.Other)
                && AssetImporters.Find(importers, path) is { } importer)
            {
                RunImporter(importer, path, profile!, cache, output, manifest, errors);
            }

            if (Reusable(importers, classification, path) && errors.Count == 0)
            {
                index.Record(_fileSystem, path, Relative(path), manifest.Assets[produced..]);
            }
        }

        if (errors.Count > 0) return new BuildResult(false, errors, manifest.Assets.Count, output);

        // Only after a clean build. An index written beside a tree that failed halfway would claim
        // outputs that were never produced, and the next run would trust it.
        index.Save(_fileSystem, output);

        manifest.Save(_fileSystem, output / BuildManifest.FileName);
        return new BuildResult(true, [], manifest.Assets.Count, output);
    }

    /// <summary>
    /// Runs one importer over one asset. The importer writes the output mount directly; what it
    /// ACTUALLY wrote — observed, not reported — becomes the manifest entries.
    /// </summary>
    private void RunImporter(IAssetImporter importer, UPath path, BuildProfile profile, ArtifactCache cache, UPath output, BuildManifest manifest, List<string> errors)
    {
        // A sidecar has no sidecar of its own — that lookup would chase x.meta.meta and throw.
        // Everything else has one, or verify refused the build before this ran.
        var meta = SidecarMeta.IsSidecarPath(path) ? null : SidecarMeta.Load(_fileSystem, SidecarMeta.PathFor(path));

        using var observed = new RecordingFileSystem(_fileSystem, output);
        var context = new ImportContext(
            _fileSystem, _layout.Assets, path, Relative(path), meta,
            profile, observed, cache, _encoder, _log);

        importer.Import(context, errors);

        foreach (var written in observed.Written)
        {
            var bytes = observed.ReadAllBytes(written);
            manifest.Assets.Add(new BuiltAsset
            {
                Path = written.FullName[1..],
                Source = context.Source,
                Guid = importer.RecordsIdentity && meta is { } identified ? DocumentGuid.Format(identified.Guid) : null,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                Size = bytes.Length,
            });
        }
    }

    /// <summary>
    /// Whether <see cref="BuildIndex"/> may answer for this asset — true only when the source
    /// bytes (plus its sidecar, which the index also keys on) are the step's COMPLETE input.
    /// </summary>
    /// <remarks>
    /// The importer answers (<see cref="IAssetImporter.DeterministicCopy"/>): only the step
    /// knows whether tool versions, profile flags or referenced files are part of its input.
    /// The manifest, unclaimed files, and anything the target's set does not build find no
    /// importer and are never reused.
    /// </remarks>
    private static bool Reusable(IReadOnlyList<IAssetImporter> importers, AssetClass classification, UPath path)
        => classification is not (AssetClass.Manifest or AssetClass.Other)
            && AssetImporters.Find(importers, path) is { DeterministicCopy: true };

    private string Relative(UPath path) => path.FullName[(_layout.Assets.FullName.Length + 1)..];
}
