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
/// one process every asset gets: offer it to the import chain — LAST importer first, until one
/// answers <see langword="true"/> — with an <see cref="ImportContext"/> whose output is the
/// build tree mounted and observed (<see cref="RecordingFileSystem"/>), then record what was
/// actually written. There is no lookup table and no per-target set: what an asset IS, and
/// whether this tree is the one it belongs in, live entirely inside the importers. Nothing here
/// changes when the chain grows, and a project that appends an importer shadows the built-in it
/// replaces without touching this file.
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
    private readonly IReadOnlyList<IAssetImporter> _importers;

    /// <summary>Creates a runner for one project.</summary>
    /// <param name="fileSystem">The filesystem holding the project.</param>
    /// <param name="layout">The located project.</param>
    /// <param name="encoder">
    /// The texture encoder, or <see langword="null"/> when no <c>ktx</c> is available — the
    /// build then fails if (and only if) there are textures to encode.
    /// </param>
    /// <param name="log">Progress lines.</param>
    /// <param name="warn">Non-fatal trouble (also handed to the artifact cache).</param>
    /// <param name="importers">
    /// The import chain, lowest precedence first; <see cref="AssetImporters.All"/> when omitted.
    /// A project extends the pipeline by APPENDING to that list — appended means later means
    /// offered the asset first, so its own importer shadows the built-in it replaces.
    /// </param>
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

    /// <summary>Builds the named profile into <paramref name="target"/>'s tree.</summary>
    /// <param name="profileName">
    /// A profile declared in <c>project.toml</c>, or <see langword="null"/> for
    /// <see cref="BuildProfile.Default"/>.
    /// </param>
    /// <param name="target">Which build-shaped tree to write.</param>
    /// <remarks>
    /// <b>Naming a profile and not naming one are different requests, and null is how the
    /// difference is said.</b> A name the manifest does not declare is an error however
    /// plausible it sounds — the caller asked for something that does not exist. Null asks for
    /// nothing in particular and gets the defaults, so a manifest that declares no profiles at
    /// all is still buildable. What this must NOT do is bless a particular name: deciding what
    /// an absent <c>--profile</c> means belongs to whoever left it absent, and a library that
    /// guesses at one English word is a library the CLI can silently fall out of step with.
    /// </remarks>
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
        // "" for an unnamed build, which is also BuildManifest's own default for the field. It
        // cannot be mistaken for a declared profile: the manifest reader refuses an empty name.
        var manifest = new BuildManifest { Project = projectManifest.Name, Profile = profileName ?? "" };
        if (!_fileSystem.DirectoryExists(output)) _fileSystem.CreateDirectory(output);

        var index = BuildIndex.Load(_fileSystem, output, profileName, target);

        foreach (var path in _fileSystem.EnumerateFiles(_layout.Assets, "*", SearchOption.AllDirectories).OrderBy(p => p.FullName, StringComparer.Ordinal))
        {
            // Source sidecars stay in assets/. The built tree's identity database is the
            // manifest — copying them next to play artifacts was a second copy of the same
            // facts, and a checkout cannot make this one dirty.
            if (SidecarMeta.IsSidecarPath(path)) continue;

            // Asked of everything else, with no further gate. The index only ever holds what a
            // previous run RECORDED, and recording is gated below on the handling importer's
            // DeterministicCopy -- so a texture, a document, an unclaimed file or the manifest
            // simply misses, and the one rule about what may be reused lives in one place
            // instead of being asserted twice from opposite ends of the loop.
            if (index.TryReuse(_fileSystem, path, Relative(path), output, out var already))
            {
                manifest.Assets.AddRange(already);
                continue;
            }

            // What this source produced, taken as the entries the importer's writes append. Done
            // here rather than inside any importer, so none of them knows an index exists.
            var produced = manifest.Assets.Count;

            // ONE process for every asset: offer it to the chain and let an importer claim it.
            // The runner does not know what anything IS, which tree wants it, or that the
            // manifest is special -- an asset nobody claims is simply built by nobody.
            var handler = Offer(path, profile!, target, cache, output, manifest, errors);

            if (handler is { DeterministicCopy: true } && errors.Count == 0)
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
    /// Offers one asset to the chain, last importer first, and stops at the one that claims it.
    /// The importer writes the output mount directly; what it ACTUALLY wrote — observed, not
    /// reported — becomes the manifest entries.
    /// </summary>
    /// <returns>The importer that handled the asset, or <see langword="null"/> when none did.</returns>
    /// <remarks>
    /// Backwards, because a chain is extended by APPENDING: the last row is the most specific
    /// claim anyone has made, so it is asked first and a project's own importer beats the
    /// built-in it shadows. One <see cref="ImportContext"/> serves every attempt — declining is
    /// each importer's first act, so nothing is written before the claim is settled, and a
    /// decline that wrote anyway surfaces in the manifest rather than vanishing.
    /// </remarks>
    private IAssetImporter? Offer(UPath path, BuildProfile profile, ProjectOutputTarget target, ArtifactCache cache, UPath output, BuildManifest manifest, List<string> errors)
    {
        // A sidecar has no sidecar of its own — that lookup would chase x.meta.meta and throw.
        // Everything else has one, or verify refused the build before this ran.
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
