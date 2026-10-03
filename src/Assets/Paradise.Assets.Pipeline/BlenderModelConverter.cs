using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Paradise.Assets.Documents;

namespace Paradise.Assets.Pipeline;

/// <summary>A model, animation or scene file headless Blender imports, to GLB, stamped in the GLB's <c>asset.extras</c> with what it was made from.</summary>
/// <remarks>
/// <para>
/// The stamp is the source's SHA-256, <see cref="ConverterVersion"/>, the Blender version and every
/// external file Blender loaded while importing (textures, libraries, a <c>.mtl</c>, <c>.bin</c>
/// buffers) with its SHA-256: the exporter's output changes between Blender releases, the script's
/// between converter versions, and the GLB with any file it was made from. The Blender addon reads
/// the same keys, so they are a cross-language contract.
/// </para>
/// <para>
/// A <c>.blend</c> holding collections marked as assets is one model per such collection: each
/// exports alone, relative to the collection's <c>instance_offset</c>, to a GLB named by the GUID
/// the collection carries in its <c>paradise_guid</c> custom property. Only tooling mints that GUID
/// (the Blender addon on save), so a collection without one, or two sharing one, fails the
/// conversion. Each GLB is stamped besides with its asset's GUID
/// (<c>paradiseAsset</c>) and name (<c>paradiseAssetName</c>) and every asset of the file as
/// <c>{ guid, name }</c> (<c>paradiseAssets</c>), so any one of its GLBs tells which models the
/// file held when it was converted.
/// </para>
/// </remarks>
public static class BlenderModelConverter
{
    public const string BlenderPathEnvironmentVariable = "PARADISE_BLENDER_PATH";

    /// <summary>Bumped whenever the script or its export settings change, so every GLB made by an earlier one converts again.</summary>
    public const int ConverterVersion = 4;

    internal const string SourceSha256Extra = "paradiseSourceSha256";
    internal const string ConverterVersionExtra = "paradiseConverterVersion";
    internal const string BlenderVersionExtra = "paradiseBlenderVersion";
    internal const string DependenciesExtra = "paradiseDependencies";
    internal const string AssetExtra = "paradiseAsset";
    internal const string AssetNameExtra = "paradiseAssetName";
    internal const string AssetsExtra = "paradiseAssets";

    private const int BlenderTimeoutMilliseconds = 30 * 60 * 1000;

    /// <summary>
    /// Each converted extension and the Python call that imports <c>source</c> into an empty scene;
    /// null for a <c>.blend</c>, which Blender opens as the main file instead. Importer defaults
    /// otherwise, axes and scale included: each already maps its format onto Blender's Z-up scene,
    /// and the glTF exporter maps that onto the pipeline's Y-up.
    /// </summary>
    private static readonly (string Extension, string? Import)[] s_importers =
    [
        (".blend", null),
        (".fbx", "bpy.ops.import_scene.fbx(filepath=source, automatic_bone_orientation=True)"),
        (".obj", "bpy.ops.wm.obj_import(filepath=source)"),
        (".ply", "bpy.ops.wm.ply_import(filepath=source)"),
        (".stl", "bpy.ops.wm.stl_import(filepath=source)"),
        (".usd", "bpy.ops.wm.usd_import(filepath=source)"),
        (".usda", "bpy.ops.wm.usd_import(filepath=source)"),
        (".usdc", "bpy.ops.wm.usd_import(filepath=source)"),
        (".usdz", "bpy.ops.wm.usd_import(filepath=source)"),
        (".abc", "bpy.ops.wm.alembic_import(filepath=source)"),
        (".bvh", "bpy.ops.import_anim.bvh(filepath=source)"),
    ];

    /// <summary>Every extension converted through Blender, lowercase with the dot, in table order.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [.. s_importers.Select(entry => entry.Extension)];

    /// <summary>
    /// The oldest Blender the script runs on: <c>bpy.data.file_path_map</c>, which lists what an
    /// import read, arrived in 4.4 (it is absent from the 4.3 API reference); the <c>wm.*_import</c>
    /// operators are older.
    /// </summary>
    public static readonly Version MinimumBlenderVersion = new(4, 4);

    /// <summary>Each executable's last answer, keyed by its resolved path and held with that file's write time, so a Blender upgraded in place is asked again.</summary>
    private static readonly ConcurrentDictionary<string, (DateTime Written, string Version)> s_versions = new(StringComparer.Ordinal);

    /// <summary>One external file a conversion read: its path relative to the source's directory, <c>/</c>-separated, and its SHA-256.</summary>
    internal readonly record struct Dependency(string Path, string Sha256);

    /// <summary>What a converted GLB was made from; <paramref name="Asset"/> and <paramref name="Assets"/> are set exactly for one asset of a <c>.blend</c> with asset collections.</summary>
    internal readonly record struct SourceStamp(
        string SourceSha256, int ConverterVersion, string BlenderVersion, IReadOnlyList<Dependency> Dependencies,
        ModelAsset? Asset = null, IReadOnlyList<ModelAsset>? Assets = null);

    /// <summary>One GLB an export produced: the whole source (<paramref name="Asset"/> null) or one asset collection of a <c>.blend</c>.</summary>
    internal readonly record struct ExportedModel(ModelAsset? Asset, byte[] Glb);

    /// <summary>What Blender exported, unstamped — or, with no GLB, why it did not — and the files it read doing so, relative to the source's directory.</summary>
    /// <param name="Models">The export: the whole source alone, or one GLB per asset collection in ordinal name order; null exactly when <paramref name="Failure"/> says why there is none.</param>
    /// <param name="Dependencies">What the import read; a failed run lists what it read before failing, or nothing when it failed before listing (Blender could not open a <c>.blend</c>, or crashed).</param>
    /// <param name="Failure">Blender's own failure, named with its output.</param>
    internal readonly record struct Export(IReadOnlyList<ExportedModel>? Models, IReadOnlyList<string> Dependencies, string? Failure);

    /// <summary>
    /// The Blender to run, or null. A set <see cref="BlenderPathEnvironmentVariable"/> is the only
    /// candidate: an author who named a Blender that cannot run is told so rather than handed
    /// whichever other Blender happens to be installed.
    /// </summary>
    public static string? FindBlender()
    {
        var configured = Environment.GetEnvironmentVariable(BlenderPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured)) return ProcessTools.IsRunnable(configured) ? configured : null;
        return ProcessTools.FindExecutable(null, DefaultBlenderPaths(), "blender");
    }

    /// <summary>The first non-empty line of <c>blender --version</c>, asked again only when the executable changes; null when it does not answer, which is asked again next time.</summary>
    public static string? BlenderVersion(string blenderPath)
    {
        ArgumentNullException.ThrowIfNull(blenderPath);

        var executable = ResolvedExecutable(blenderPath);
        var written = File.GetLastWriteTimeUtc(executable);
        if (s_versions.TryGetValue(executable, out var known) && known.Written == written) return known.Version;

        var run = ProcessTools.Run(blenderPath, "--version", timeoutMilliseconds: 60_000);
        if (!run.Succeeded) return null;
        var version = run.Stdout.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);
        if (version is not null) s_versions[executable] = (written, version);
        return version;
    }

    /// <summary>The version of the Blender <see cref="FindBlender"/> finds; null when there is none or it does not answer.</summary>
    internal static string? InstalledVersion() => FindBlender() is { } blender ? BlenderVersion(blender) : null;

    /// <summary>The file a Blender path finally names: a package manager's link moves to a new target on upgrade, the link itself does not change.</summary>
    private static string ResolvedExecutable(string path)
    {
        try
        {
            return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Path.GetFullPath(path);
        }
    }

    /// <summary>
    /// Whether <paramref name="glb"/> still stands for a source with this hash: the source, the
    /// converter and every recorded dependency must match (<paramref name="dependencySha256"/>
    /// answers null for one that is gone), and so must the Blender version when there is a Blender
    /// to ask — with none, the GLB already made is the best there is. It must also be the model
    /// asked for: the GLB of the asset with GUID <paramref name="asset"/>, or with null the whole source's.
    /// </summary>
    internal static bool IsCurrent(byte[] glb, string sourceSha256, string? blenderVersion, Func<string, string?> dependencySha256, Guid? asset = null)
    {
        if (!GlbBinary.TryRead(glb, out var gltf, out _)) return false;
        if ((gltf["asset"] as JsonObject)?["extras"] is not JsonObject extras) return false;

        return extras[SourceSha256Extra] is JsonValue sha && sha.TryGetValue(out string? storedSha)
            && string.Equals(storedSha, sourceSha256, StringComparison.OrdinalIgnoreCase)
            && extras[ConverterVersionExtra] is JsonValue converter && converter.TryGetValue(out int storedConverter)
            && storedConverter == ConverterVersion
            && (blenderVersion is null
                || (extras[BlenderVersionExtra] is JsonValue blender && blender.TryGetValue(out string? storedBlender)
                    && string.Equals(storedBlender, blenderVersion, StringComparison.Ordinal)))
            && StampedAsset(extras) == asset
            && DependenciesMatch(extras, dependencySha256);
    }

    /// <summary>Every asset of the source a per-asset GLB was converted from; null for a whole-source GLB or one that is not stamped.</summary>
    internal static IReadOnlyList<ModelAsset>? StampedAssets(byte[] glb)
    {
        if (!GlbBinary.TryRead(glb, out var gltf, out _)) return null;
        if ((gltf["asset"] as JsonObject)?["extras"] is not JsonObject extras || extras[AssetsExtra] is not JsonArray listed) return null;

        var assets = new List<ModelAsset>(listed.Count);
        foreach (var node in listed)
        {
            if (node is not JsonObject entry
                || entry[ModelAsset.GuidKey] is not JsonValue guidValue || !guidValue.TryGetValue(out string? guidText)
                || !DocumentGuid.TryParse(guidText, out var guid)
                || entry[ModelAsset.NameKey] is not JsonValue nameValue || !nameValue.TryGetValue(out string? name)
                || !IsDocumentName(name))
            {
                return null;
            }

            assets.Add(new ModelAsset(guid, name));
        }

        return DistinctNames(assets) ? assets : null;
    }

    /// <summary>Two assets whose names differ only in case would share document files on a Mac or Windows disk; the script refuses them, and so does the reading side.</summary>
    private static bool DistinctNames(List<ModelAsset> assets)
        => assets.Select(asset => asset.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == assets.Count;

    /// <summary>
    /// Whether an asset's name can name its documents: extraction writes <c>&lt;name&gt;.mesh</c>,
    /// <c>&lt;name&gt;.prefab</c> and the like from it. The conversion script refuses a collection
    /// otherwise; this is the same rule on the reading side, so a converted GLB that did not come
    /// from that script (copied in, or edited) cannot send a write outside its directory.
    /// </summary>
    internal static bool IsDocumentName(string name)
        => name.Length > 0
            && name is not ("." or "..")
            && name == name.Trim()
            && !name.EndsWith('.')
            && name.All(ch => ch >= ' ' && !ReservedNameCharacters.Contains(ch));

    /// <summary>Windows' reserved file-name characters, which the conversion script refuses too.</summary>
    private const string ReservedNameCharacters = "<>:\"/\\|?*";

    /// <summary>The dependencies a converted GLB is stamped with; empty when it carries none.</summary>
    internal static IReadOnlyList<Dependency> StampedDependencies(byte[] glb)
    {
        if (!GlbBinary.TryRead(glb, out var gltf, out _)) return [];
        if ((gltf["asset"] as JsonObject)?["extras"] is not JsonObject extras || extras[DependenciesExtra] is not JsonArray dependencies) return [];

        var result = new List<Dependency>(dependencies.Count);
        foreach (var node in dependencies)
        {
            if (node is JsonObject dependency
                && dependency["path"] is JsonValue pathValue && pathValue.TryGetValue(out string? path)
                && dependency["sha256"] is JsonValue shaValue && shaValue.TryGetValue(out string? sha))
            {
                result.Add(new Dependency(path, sha));
            }
        }

        return result;
    }

    private static Guid? StampedAsset(JsonObject extras)
        => extras[AssetExtra] is JsonValue value && value.TryGetValue(out string? text) && DocumentGuid.TryParse(text, out var guid) ? guid : null;

    /// <summary>An absent list is a mismatch: only a converter before version 2 left one out, and the version check already refuses those.</summary>
    private static bool DependenciesMatch(JsonObject extras, Func<string, string?> dependencySha256)
    {
        if (extras[DependenciesExtra] is not JsonArray dependencies) return false;
        foreach (var node in dependencies)
        {
            if (node is not JsonObject dependency
                || dependency["path"] is not JsonValue pathValue || !pathValue.TryGetValue(out string? path)
                || dependency["sha256"] is not JsonValue shaValue || !shaValue.TryGetValue(out string? sha)
                || !string.Equals(dependencySha256(path), sha, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The GLB with the stamp added to <c>asset.extras</c>, everything else as it was.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a GLB.</exception>
    internal static byte[] Stamp(byte[] glb, SourceStamp stamp)
    {
        if (!GlbBinary.TryRead(glb, out var gltf, out var bin)) throw new InvalidDataException("Blender's export is not a readable GLB");

        if (gltf["asset"] is not JsonObject asset)
        {
            asset = new JsonObject();
            gltf["asset"] = asset;
        }

        if (asset["extras"] is not JsonObject extras)
        {
            extras = new JsonObject();
            asset["extras"] = extras;
        }

        extras[SourceSha256Extra] = stamp.SourceSha256;
        extras[ConverterVersionExtra] = stamp.ConverterVersion;
        extras[BlenderVersionExtra] = stamp.BlenderVersion;
        extras[DependenciesExtra] = new JsonArray([
            .. stamp.Dependencies
                .OrderBy(dependency => dependency.Path, StringComparer.Ordinal)
                .Select(dependency => (JsonNode)new JsonObject { ["path"] = dependency.Path, ["sha256"] = dependency.Sha256 }),
        ]);
        extras.Remove(AssetExtra);
        extras.Remove(AssetNameExtra);
        extras.Remove(AssetsExtra);
        if (stamp.Asset is { } own)
        {
            extras[AssetExtra] = DocumentGuid.Format(own.Guid);
            extras[AssetNameExtra] = own.Name;
            extras[AssetsExtra] = new JsonArray([
                .. (stamp.Assets ?? [own])
                    .OrderBy(each => DocumentGuid.Format(each.Guid), StringComparer.Ordinal)
                    .Select(each => (JsonNode)new JsonObject { [ModelAsset.GuidKey] = DocumentGuid.Format(each.Guid), [ModelAsset.NameKey] = each.Name }),
            ]);
        }

        return GlbBinary.Write(gltf, bin);
    }

    /// <summary>
    /// Converts the source at <paramref name="sourceFullPath"/>; nothing is written beside the source.
    /// <paramref name="extension"/> overrides the one the path ends in, so a <c>.blend</c> staged
    /// under another name is still opened as one.
    /// </summary>
    /// <exception cref="InvalidDataException">Blender exported, but did not list the files it read, or listed them as something other than paths.</exception>
    internal static Export Convert(string blenderPath, string sourceFullPath, string? extension = null)
    {
        extension ??= Path.GetExtension(sourceFullPath).ToLowerInvariant();
        var temporary = Path.Combine(Path.GetTempPath(), "ParadiseModelConvert", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var script = Path.Combine(temporary, "to_glb.py");
            var staged = Path.Combine(temporary, "staged.glb");
            var dependencies = Path.Combine(temporary, "dependencies.json");
            var assets = Path.Combine(temporary, "assets");
            var listed = Path.Combine(temporary, "assets.json");
            Directory.CreateDirectory(assets);
            File.WriteAllText(script, Script());

            // Without --python-exit-code a Python exception in the script exits 0.
            List<string> arguments = ["--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1"];

            // A .blend is opened as the main file (its embedded scripts are not the build's to run,
            // hence --disable-autoexec); every other format is imported by the script.
            if (extension == ".blend") arguments.Add(ProcessTools.QuoteArgument(sourceFullPath));

            arguments.AddRange([
                "--python", ProcessTools.QuoteArgument(script), "--",
                ProcessTools.QuoteArgument(sourceFullPath), ProcessTools.QuoteArgument(staged), ProcessTools.QuoteArgument(dependencies),
                ProcessTools.QuoteArgument(assets), ProcessTools.QuoteArgument(listed), extension,
            ]);

            var run = ProcessTools.Run(blenderPath, string.Join(' ', arguments), BlenderTimeoutMilliseconds);
            if (!run.Succeeded) return Failed(run.Describe($"Blender converting '{sourceFullPath}'", BlenderTimeoutMilliseconds));

            var perAsset = File.Exists(listed) ? ReadAssets(listed, sourceFullPath) : [];
            if (File.Exists(staged) && perAsset.Count > 0) return Failed($"Blender exported both the whole of '{sourceFullPath}' and its asset collections; a source is one or the other.\n{run.Stdout}{run.Stderr}");
            if (!File.Exists(staged) && perAsset.Count == 0) return Failed($"Blender exited 0 but exported no GLB for '{sourceFullPath}'.\n{run.Stdout}{run.Stderr}");
            if (!File.Exists(dependencies)) throw new InvalidDataException($"Blender exported '{sourceFullPath}' but did not list the files it read.\n{run.Stdout}{run.Stderr}");

            // Each asset's GLB is named by its GUID, which the script checked is canonical and unique.
            var missing = perAsset.FirstOrDefault(asset => !File.Exists(Path.Combine(assets, DocumentGuid.Format(asset.Guid) + ".glb")));
            if (missing is not null) return Failed($"Blender listed asset collection {missing} of '{sourceFullPath}' but exported no GLB for it.\n{run.Stdout}{run.Stderr}");

            IReadOnlyList<ExportedModel> models = File.Exists(staged)
                ? [new ExportedModel(null, File.ReadAllBytes(staged))]
                : [.. perAsset.Select(asset => new ExportedModel(asset, File.ReadAllBytes(Path.Combine(assets, DocumentGuid.Format(asset.Guid) + ".glb"))))];
            return new Export(models, ReadDependencies(dependencies, sourceFullPath), null);

            Export Failed(string failure) => new(null, ListedDependencies(dependencies, sourceFullPath), failure);
        }
        finally
        {
            try
            {
                Directory.Delete(temporary, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>What a failed run listed as read; none when it failed before listing anything (Blender could not open a <c>.blend</c>, or crashed) or left a list that is not one.</summary>
    private static IReadOnlyList<string> ListedDependencies(string path, string sourceFullPath)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return ReadDependencies(path, sourceFullPath);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The script's list of the asset collections it exported, <c>[{ guid, name }]</c>, in ordinal name order.</summary>
    /// <exception cref="InvalidDataException">The list is not one.</exception>
    private static List<ModelAsset> ReadAssets(string path, string sourceFullPath)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonArray listed)
            {
                var assets = new List<ModelAsset>(listed.Count);
                foreach (var node in listed)
                {
                    if (node is not JsonObject entry
                        || entry[ModelAsset.GuidKey] is not JsonValue guidValue || !guidValue.TryGetValue(out string? guidText)
                        || !DocumentGuid.TryParse(guidText, out var guid)
                        || entry[ModelAsset.NameKey] is not JsonValue nameValue || !nameValue.TryGetValue(out string? name)
                        || !IsDocumentName(name))
                    {
                        break;
                    }

                    assets.Add(new ModelAsset(guid, name));
                }

                if (assets.Count == listed.Count && DistinctNames(assets)) return [.. assets.OrderBy(asset => asset.Name, StringComparer.Ordinal)];
            }
        }
        catch (JsonException)
        {
        }

        throw new InvalidDataException($"the list of asset collections Blender exported converting '{sourceFullPath}' is not a JSON array of {{ guid, name }}");
    }

    /// <summary>The script's list of the files the import read: a JSON array of strings.</summary>
    /// <exception cref="InvalidDataException">The list is not one.</exception>
    private static IReadOnlyList<string> ReadDependencies(string path, string sourceFullPath)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonArray listed)
            {
                var paths = new List<string>(listed.Count);
                foreach (var node in listed)
                {
                    if (node is not JsonValue value || !value.TryGetValue(out string? dependency)) break;
                    paths.Add(dependency);
                }

                if (paths.Count == listed.Count) return paths;
            }
        }
        catch (JsonException)
        {
        }

        throw new InvalidDataException($"the list of files Blender read converting '{sourceFullPath}' is not a JSON array of paths");
    }

    /// <summary>The conversion script: the import table as a dispatch dictionary, then the GLB export and the list of files the import read.</summary>
    private static string Script()
    {
        var importers = new StringBuilder();
        foreach (var (extension, import) in s_importers)
        {
            if (import is not null) importers.Append(CultureInfo.InvariantCulture, $"    '{extension}': lambda source: {import},\n");
        }

        return ScriptTemplate
            .Replace("#MINIMUM#", $"({MinimumBlenderVersion.Major}, {MinimumBlenderVersion.Minor})", StringComparison.Ordinal)
            .Replace("#IMPORTERS#\n", importers.ToString(), StringComparison.Ordinal);
    }

    private const string ScriptTemplate = """
        import json
        import os
        import re
        import sys

        import bpy

        MINIMUM = #MINIMUM#
        if tuple(bpy.app.version[:2]) < MINIMUM:
            sys.exit(f"paradise: converting needs Blender {MINIMUM[0]}.{MINIMUM[1]} or newer (bpy.data.file_path_map); "
                     f"this is Blender {bpy.app.version_string}")

        source, glb_out, dependencies_out, assets_out, assets_list_out, extension = sys.argv[sys.argv.index('--') + 1:][:6]

        IMPORTERS = {
        #IMPORTERS#
        }

        def candidates():
            # Blender's own record of the external files its datablocks name: images, libraries, caches.
            for datablock, paths in bpy.data.file_path_map(include_libraries=True).items():
                for path in paths:
                    yield bpy.path.abspath(path, library=datablock.library)
            # An importer may pack what it read (USD does by default), which drops it from that map;
            # the file is still an input. An image a .blend itself packed is not: it reads no file.
            if extension != '.blend':
                for image in bpy.data.images:
                    if image.source == 'FILE' and image.filepath:
                        yield bpy.path.abspath(image.filepath, library=image.library)

            # Files an importer reads without leaving a datablock that names them.
            directory = os.path.dirname(source)
            if extension == '.obj':
                with open(source, encoding='utf-8', errors='replace') as obj:
                    for line in obj:
                        if line.startswith('mtllib'):
                            yield os.path.join(directory, line[len('mtllib'):].strip())
            elif extension in ('.usd', '.usda', '.usdc'):
                from pxr import UsdUtils
                layers, assets, _ = UsdUtils.ComputeAllDependencies(source)
                for layer in layers:
                    if layer.realPath:
                        yield layer.realPath
                yield from assets


        def list_dependencies():
            source_real = os.path.realpath(source)
            source_directory = os.path.dirname(source_real)
            found = set()
            for candidate in candidates():
                real = os.path.realpath(candidate)
                if real == source_real or not os.path.isfile(real):
                    continue
                try:
                    found.add(os.path.relpath(real, source_directory).replace(os.sep, '/'))
                except ValueError:
                    # Another drive: no relative path exists, and an absolute one resolves as itself.
                    found.add(real.replace(os.sep, '/'))

            with open(dependencies_out, 'w', encoding='utf-8') as out:
                json.dump(sorted(found), out)


        def export(path, **options):
            bpy.ops.export_scene.gltf(
                filepath=path,
                export_format='GLB',
                export_yup=True,
                export_apply=True,
                export_animations=True,
                # Explicit tangents avoid GltfSceneReader's constant fallback, which cannot support normal maps correctly.
                export_tangents=True,
                **options,
            )


        # A new model's name becomes its documents' file names, so it has to be one on every platform:
        # none of Windows' reserved characters, no control characters, no surrounding space, no
        # trailing dot, and unique ignoring case, as a Mac or Windows disk compares names.
        RESERVED = set('<>:"/\\|?*')

        # The asset's identity, minted only by tooling (the Paradise Assets addon on save):
        # a canonical GUID, which also names the asset's GLB.
        GUID_PROPERTY = 'paradise_guid'
        CANONICAL_GUID = re.compile(r'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')
        NIL_GUID = '00000000-0000-0000-0000-000000000000'


        def asset_guid(collection):
            guid = collection.get(GUID_PROPERTY)
            file = os.path.basename(source)
            if guid is None:
                sys.exit(f"paradise: asset collection '{collection.name}' in {file} has no Paradise GUID; "
                         f"save it once in Blender with the Paradise Assets addon enabled")
            if not isinstance(guid, str) or not CANONICAL_GUID.match(guid) or guid == NIL_GUID:
                sys.exit(f"paradise: asset collection '{collection.name}' in {file} has Paradise GUID {guid!r}, which is "
                         f"not a lowercase hyphenated GUID; save it once in Blender with the Paradise Assets addon enabled")
            return guid


        def asset_collections():
            if extension != '.blend':
                return []
            found = sorted((c for c in bpy.data.collections if c.asset_data is not None and c.library is None),
                           key=lambda c: c.name)
            folded = {}
            for collection in found:
                name = collection.name
                if (not name or name in ('.', '..') or name != name.strip() or name.endswith('.')
                        or any(ch in RESERVED or ord(ch) < 32 for ch in name)):
                    sys.exit(f"paradise: asset collection '{name}' cannot name the model's documents; rename it without "
                             f"<>:\"/\\|?*, control characters, surrounding spaces or a trailing dot")
                if name.casefold() in folded:
                    sys.exit(f"paradise: asset collections '{folded[name.casefold()]}' and '{name}' differ only in "
                             f"case, so their documents would share a file name; rename one")
                folded[name.casefold()] = name
            # Blender copies custom properties with a duplicated collection, so two can carry one GUID.
            owners = {}
            for collection in found:
                guid = asset_guid(collection)
                if guid in owners:
                    sys.exit(f"paradise: asset collections '{owners[guid]}' and '{collection.name}' in "
                             f"{os.path.basename(source)} share Paradise GUID {guid}, so they would be one model; "
                             f"save it once in Blender with the Paradise Assets addon enabled to give the copy its own")
                owners[guid] = collection.name
            # An asset exports with every collection under it, so a nested asset would be in both models.
            names = {collection.name for collection in found}
            for parent in found:
                nested = sorted(c.name for c in parent.children_recursive if c.library is None and c.name in names)
                if nested:
                    sys.exit(f"paradise: asset collection '{nested[0]}' is nested in asset collection '{parent.name}', "
                             f"so its objects would be in both models; move it out of '{parent.name}' or clear "
                             f"the asset mark of one of them")
            return found


        def layers_to(layer, collection):
            # The layer collections from the view layer's root down to the collection's, or None.
            if layer.collection == collection:
                return [layer]
            for child in layer.children:
                found = layers_to(child, collection)
                if found is not None:
                    return [layer] + found
            return None


        def export_asset(collection, path):
            scene = bpy.context.scene
            view_layer = bpy.context.view_layer
            linked = layers_to(view_layer.layer_collection, collection) is None
            if linked:
                scene.collection.children.link(collection)
            # An excluded ancestor leaves the collection out of the view layer as surely as its own
            # exclude does. The root is the scene's collection, which cannot be excluded.
            layers = layers_to(view_layer.layer_collection, collection)
            layer = layers[-1]
            excluded = [(ancestor, ancestor.exclude) for ancestor in layers[1:]]
            for ancestor, _ in excluded:
                ancestor.exclude = False

            # The collection's instance offset is the asset's origin. Its roots move by the opposite
            # through delta_location, which an animation of location does not override; a root
            # parented outside the asset moves in its parent's space.
            members = set(collection.all_objects)
            shifted = []
            for obj in members:
                if obj.parent in members:
                    continue
                shift = -collection.instance_offset
                if obj.parent is not None:
                    shift = (obj.parent.matrix_world @ obj.matrix_parent_inverse).to_3x3().inverted_safe() @ shift
                shifted.append((obj, obj.delta_location.copy()))
                obj.delta_location = obj.delta_location + shift

            view_layer.active_layer_collection = layer
            try:
                export(path, use_active_scene=True, use_active_collection=True, use_active_collection_with_nested=True)
            finally:
                for obj, delta in shifted:
                    obj.delta_location = delta
                for ancestor, was in reversed(excluded):
                    ancestor.exclude = was
                if linked:
                    scene.collection.children.unlink(collection)


        # Listed when the import or export fails too: a failure caused by a file the import read (a
        # malformed .mtl) is retried once that file changes, not only once the source does.
        try:
            # A .blend arrives already open as the main file; everything else is imported into an empty scene.
            if extension in IMPORTERS:
                bpy.ops.wm.read_factory_settings(use_empty=True)
                IMPORTERS[extension](source)

            # A .blend with asset collections is one model per collection, and what lies outside
            # them is not a model; any other source is one model, all of it.
            assets = asset_collections()
            if not assets:
                export(glb_out)
            for collection in assets:
                export_asset(collection, os.path.join(assets_out, asset_guid(collection) + '.glb'))
            if assets:
                with open(assets_list_out, 'w', encoding='utf-8') as out:
                    json.dump([{'guid': asset_guid(c), 'name': c.name} for c in assets], out)
        finally:
            list_dependencies()
        """;

    private static IEnumerable<string> DefaultBlenderPaths()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Blender.app/Contents/MacOS/Blender";
            yield return "/opt/homebrew/bin/blender";
            yield return "/usr/local/bin/blender";
        }
        else if (OperatingSystem.IsWindows())
        {
            foreach (var programFiles in new[]
                     {
                         Environment.GetEnvironmentVariable("ProgramFiles"),
                         Environment.GetEnvironmentVariable("ProgramW6432"),
                     })
            {
                if (string.IsNullOrWhiteSpace(programFiles)) continue;

                var foundation = Path.Combine(programFiles, "Blender Foundation");
                if (!Directory.Exists(foundation)) continue;

                foreach (var candidate in Directory.EnumerateFiles(foundation, "blender.exe", SearchOption.AllDirectories))
                {
                    yield return candidate;
                }
            }
        }
        else
        {
            yield return "/usr/bin/blender";
            yield return "/usr/local/bin/blender";
        }
    }
}
