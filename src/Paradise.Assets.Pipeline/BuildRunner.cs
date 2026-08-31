using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Export.Serialization;

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
/// Steps implemented so far — textures (PNG/JPEG → KTX2 through the content-addressed cache;
/// preset from the sidecar, filename tokens as the fallback), audio (verified copy-through),
/// meshes (copy-through, <b>refusing</b> GLBs with embedded PNG/JPEG until the externalization
/// step lands — a silently copied one would fail in the runtime's KTX2-only reader with far
/// less context), and config documents (canonical TOML for <c>document_format = "toml"</c>
/// profiles). Scene documents <b>refuse the build</b> until the bake exists: a build tree
/// missing its scenes is not a build, and pretending otherwise would make every consumer
/// handle a half-tree.
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

        foreach (var path in _fileSystem.EnumerateFiles(_layout.Assets, "*", SearchOption.AllDirectories).OrderBy(p => p.FullName, StringComparer.Ordinal))
        {
            var classification = AssetClassifier.Classify(_layout.Assets, path);

            // Only what the index may legitimately answer for -- see BuildIndex's remarks for why
            // textures and documents are excluded, and why excluding them is the point.
            if (Reusable(classification, path)
                && index.TryReuse(_fileSystem, path, Relative(path), output, out var already))
            {
                manifest.Assets.AddRange(already);
                continue;
            }

            // What this source produced, taken as the entries the dispatch below appends. Doing it
            // here rather than inside each builder keeps every Build*/Copy* method unaware that an
            // index exists -- one place to be wrong instead of five.
            var produced = manifest.Assets.Count;

            switch (classification)
            {
                case AssetClass.Foreign when AssetClassifier.TryGetForeignKind(path, out var kind):
                    switch (kind)
                    {
                        case SidecarAssetKind.Texture:
                            BuildTexture(path, profile!, cache, output, manifest, errors);
                            break;

                        case SidecarAssetKind.Mesh:
                            BuildMesh(path, output, manifest, errors);
                            break;

                        case SidecarAssetKind.Audio:
                            CopyThrough(path, output, manifest);
                            break;
                    }

                    break;

                case AssetClass.Prefab:
                    BuildPrefab(path, profile!, output, manifest, errors);
                    break;

                case AssetClass.Config:
                    BuildConfig(path, profile!, output, manifest, errors);
                    break;

                // Sidecars travel only into the EDITOR tree. They carry authoring identity, which
                // the editor's playmode wants -- it is how a built asset is traced back to the
                // document that produced it -- and which a player's install has no use for at all.
                // Shipping them would put source-tree bookkeeping in the game.
                case AssetClass.Sidecar when target == ProjectOutputTarget.Play:
                    CopySidecar(path, output, manifest);
                    break;
            }

            if (Reusable(classification, path) && errors.Count == 0)
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

    private void BuildTexture(UPath path, BuildProfile profile, ArtifactCache cache, UPath output, BuildManifest manifest, List<string> errors)
    {
        var source = Relative(path);
        var destination = output / Path.ChangeExtension(source, ".ktx2");
        var meta = SidecarMeta.Load(_fileSystem, SidecarMeta.PathFor(path));
        var preset = meta.Texture?.Preset ?? DefaultPresetFor(path);
        var fast = profile.TextureQuality == TextureQuality.Fast;
        var bytes = _fileSystem.ReadAllBytes(path);

        // The COMPLETE input of the step this key skips: source bytes, the exact argv shape
        // (which encodes preset and quality), and the tool itself.
        var argvToken = KtxCreate.BuildCreateArguments(KtxTextureEncoder.ToKtxPreset(preset), "out.ktx2", "in" + path.GetExtensionWithDot(), fast);
        var key = ArtifactDigest.Compute(bytes, argvToken, _encoder?.Identity ?? "");

        CreateParent(destination);
        if (_encoder is not null && cache.TryFetch("ktx2", key, _fileSystem, destination))
        {
            Record(manifest, destination, output, source, meta.Guid);
            return;
        }

        if (_encoder is null)
        {
            errors.Add(
                $"{source}: no ktx CLI available to encode textures — run tools/ktx/KtxBootstrap, " +
                $"install KTX-Software, or set {KtxCreate.KtxPathEnvironmentVariable}");
            return;
        }

        if (!_encoder.TryEncode(bytes, path.GetExtensionWithDot()!, preset, fast, out var ktx2, out var error))
        {
            errors.Add($"{source}: texture encode failed: {error}");
            return;
        }

        _fileSystem.WriteAllBytes(destination, ktx2);
        cache.Store("ktx2", key, _fileSystem, destination);
        _log?.Invoke($"ktx2: {source} ({ktx2.Length} bytes)");
        Record(manifest, destination, output, source, meta.Guid);
    }

    private void BuildMesh(UPath path, UPath output, BuildManifest manifest, List<string> errors)
    {
        var source = Relative(path);
        var bytes = _fileSystem.ReadAllBytes(path);
        if (HasEmbeddedEncodedImages(bytes, out var mimeType))
        {
            // A copied-through GLB with PNG/JPEG inside would fail at load: the runtime's
            // texture path is KTX2-only by design. Refusing here keeps the error at the build,
            // with the file named, instead of in a renderer log.
            errors.Add($"{source}: has embedded {mimeType} textures; the mesh externalization step is not implemented yet");
            return;
        }

        // The mesh names its textures as the author has them (../textures/rust.png); the build
        // writes them as KTX2 at the same relative place, so the copy has to be repointed.
        var rewrite = MeshTextureReferences.Rewrite(bytes);

        // Checked against the SOURCE tree, not the output: build order is alphabetical, so
        // Models/ is compiled before textures/ and the KTX2 does not exist yet. What matters is
        // that a source exists to compile at all — a reference naming nothing is a broken mesh
        // however the steps are ordered.
        var directory = path.GetDirectory();
        foreach (var reference in rewrite.Sources)
        {
            if (_fileSystem.FileExists(Resolve(directory, reference))) continue;

            errors.Add(
                $"{source}: references texture '{reference}', which does not exist under assets/ " +
                "(a moved or renamed texture; the mesh and the reference move together)");
        }

        CopyThrough(path, output, manifest, rewrite.Glb);
    }

    /// <summary>
    /// A glTF URI as a path in the assets tree. glTF URIs are '/'-separated and percent-encoded
    /// per the spec, and they are relative to the referencing document — never to the project
    /// root — which is what makes <c>../textures/x.png</c> mean what an author expects.
    /// </summary>
    private static UPath Resolve(UPath directory, string uri)
        => (directory / Uri.UnescapeDataString(uri)).ToAbsolute();

    /// <summary>The extension an authored document gets in the build, per the profile.</summary>
    private static string DocumentExtension(BuildProfile profile)
        => profile.DocumentFormat == DocumentFormat.Json ? ".json" : ".toml";

    private void BuildConfig(UPath path, BuildProfile profile, UPath output, BuildManifest manifest, List<string> errors)
    {
        var source = Relative(path);
        if (profile.DocumentFormat is not (DocumentFormat.Toml or DocumentFormat.Json))
        {
            errors.Add($"{source}: document_format \"{profile.DocumentFormat}\" output is not implemented yet (toml and json are)");
            return;
        }

        if (!ConfigDocument.TryCanonicalize(_fileSystem.ReadAllText(path), out var canonical, out var error))
        {
            errors.Add($"{source}: {error}");
            return;
        }

        // Canonicalized first either way: the TOML reader is the one strict parser, so a document
        // that would be refused as source is refused whichever format it is compiled into.
        var destination = output / Path.ChangeExtension(source, DocumentExtension(profile));
        CreateParent(destination);
        _fileSystem.WriteAllText(
            destination,
            profile.DocumentFormat == DocumentFormat.Json ? ConfigDocument.ToJson(canonical, source) : canonical);

        Record(manifest, destination, output, source, guid: null);
    }

    /// <summary>
    /// Compiles one authoring document into the export contract the runtime loads.
    /// </summary>
    /// <remarks>
    /// <b>Every document is baked, not just the ones a game calls levels.</b> There is one kind of
    /// document, so a prop compiles to a one-entity level and can be played on its own — which is
    /// the whole point of having one kind. A prefab referenced by another is ALSO flattened into
    /// it, so the same objects appear in both outputs; that is what an instance means.
    /// </remarks>
    private void BuildPrefab(UPath path, BuildProfile profile, UPath output, BuildManifest manifest, List<string> errors)
    {
        var source = Relative(path);
        if (profile.DocumentFormat != DocumentFormat.Json)
        {
            errors.Add(
                $"{source}: document_format \"{profile.DocumentFormat}\" cannot express the export contract — " +
                "set the profile to json (the format the runtime's reader takes)");
            return;
        }

        PrefabDocument document;
        try
        {
            document = PrefabDocumentSerializer.Load(_fileSystem, path);
        }
        catch (PrefabDocumentException error)
        {
            errors.Add(error.Message);
            return;
        }

        var failures = new List<string>();
        var level = PrefabBake.Bake(document, Referenced, DocumentExtension(profile), failures);
        if (failures.Count > 0)
        {
            foreach (var failure in failures) errors.Add($"{source}: {failure}");
            return;
        }

        var destination = output / Path.ChangeExtension(source, ".json");
        CreateParent(destination);
        _fileSystem.WriteAllText(destination, ExportJsonWriter.SerializeToString(level));

        var meta = SidecarMeta.Load(_fileSystem, SidecarMeta.PathFor(path));
        Record(manifest, destination, output, source, meta.Guid);

        PrefabDocument? Referenced(Paradise.Authoring.AssetReference reference)
        {
            try
            {
                return PrefabDocumentSerializer.Load(_fileSystem, _layout.Assets / reference.Path);
            }
            catch (PrefabDocumentException)
            {
                return null;   // reported against the referenced document, which is also built
            }
        }
    }

    /// <summary>
    /// Whether <see cref="BuildIndex"/> may answer for this asset — true only when the source
    /// bytes (plus its sidecar, which the index also keys on) are the step's COMPLETE input.
    /// </summary>
    /// <remarks>
    /// Meshes, audio and sidecars are copies: output is input. Textures are excluded because their
    /// encode depends on the argv and the encoder's version, which <see cref="ArtifactCache"/>
    /// already keys on and this does not. Documents are excluded because a prefab bakes the
    /// prefabs it instances, so its output changes when a file it merely REFERENCES does — and
    /// nothing about the level's own bytes says so.
    /// </remarks>
    private static bool Reusable(AssetClass classification, UPath path) => classification switch
    {
        AssetClass.Sidecar => true,
        AssetClass.Foreign => AssetClassifier.TryGetForeignKind(path, out var kind)
            && kind is SidecarAssetKind.Mesh or SidecarAssetKind.Audio,
        _ => false,
    };

    /// <summary>Copies a <c>*.meta</c> into the output tree verbatim.</summary>
    /// <remarks>
    /// Not <see cref="CopyThrough"/>, which loads the asset's sidecar to record its identity: a
    /// sidecar has no sidecar of its own, so that lookup would go looking for <c>x.meta.meta</c>
    /// and throw. The manifest entry carries a null guid for the same reason the file exists --
    /// a sidecar DESCRIBES an identity rather than having one, and recording the guid it names
    /// would give two manifest entries the same value and break any guid-to-asset lookup.
    /// </remarks>
    private void CopySidecar(UPath path, UPath output, BuildManifest manifest)
    {
        var source = Relative(path);
        var destination = output / source;
        CreateParent(destination);
        _fileSystem.WriteAllBytes(destination, _fileSystem.ReadAllBytes(path));
        Record(manifest, destination, output, source, guid: null);
    }

    private void CopyThrough(UPath path, UPath output, BuildManifest manifest, byte[]? bytes = null)
    {
        var source = Relative(path);
        var destination = output / source;
        CreateParent(destination);
        _fileSystem.WriteAllBytes(destination, bytes ?? _fileSystem.ReadAllBytes(path));

        var meta = SidecarMeta.Load(_fileSystem, SidecarMeta.PathFor(path));
        Record(manifest, destination, output, source, meta.Guid);
    }

    private void Record(BuildManifest manifest, UPath destination, UPath output, string source, Guid? guid)
    {
        var bytes = _fileSystem.ReadAllBytes(destination);
        manifest.Assets.Add(new BuiltAsset
        {
            Path = destination.FullName[(output.FullName.Length + 1)..],
            Source = source,
            Guid = guid is { } value ? DocumentGuid.Format(value) : null,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Size = bytes.Length,
        });
    }

    private void CreateParent(UPath path)
    {
        var directory = path.GetDirectory();
        if (!_fileSystem.DirectoryExists(directory)) _fileSystem.CreateDirectory(directory);
    }

    private string Relative(UPath path) => path.FullName[(_layout.Assets.FullName.Length + 1)..];

    /// <summary>
    /// The filename-token defaults the sidecar's <c>preset</c> overrides — the same heuristics
    /// <see cref="KtxCreate.PresetFromImageName"/> applies to GLB-internal images, applied to
    /// the file's stem.
    /// </summary>
    private static TexturePreset DefaultPresetFor(UPath path)
    {
        var image = new JsonObject { ["name"] = Path.GetFileNameWithoutExtension(path.GetName()) };
        return KtxCreate.PresetFromImageName(image) switch
        {
            KtxCreate.TextureEncodingPreset.UastcNormalLinear => TexturePreset.Normal,
            KtxCreate.TextureEncodingPreset.UastcDataLinear => TexturePreset.Data,
            KtxCreate.TextureEncodingPreset.UastcColorLinear => TexturePreset.ColorLinear,
            _ => TexturePreset.Color,
        };
    }

    /// <summary>
    /// Whether a GLB carries embedded (buffer-view-backed) PNG/JPEG images.
    /// </summary>
    internal static bool HasEmbeddedEncodedImages(byte[] glb, out string mimeType)
    {
        mimeType = "";
        if (!GlbBinary.TryRead(glb, out var gltf, out _)) return false;
        if (gltf["images"] is not JsonArray images) return false;
        foreach (var image in images)
        {
            var mime = image?["mimeType"]?.GetValue<string>();
            if (image?["bufferView"] is not null && mime is "image/png" or "image/jpeg")
            {
                mimeType = mime;
                return true;
            }
        }

        return false;
    }
}
