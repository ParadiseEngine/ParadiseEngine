using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Paradise.Assets.Pipeline;

/// <summary>A model, animation or scene file headless Blender imports, to GLB, stamped in the GLB's <c>asset.extras</c> with what it was made from.</summary>
/// <remarks>
/// The stamp is the source's SHA-256, <see cref="ConverterVersion"/>, the Blender version and every
/// external file Blender loaded while importing (textures, libraries, a <c>.mtl</c>, <c>.bin</c>
/// buffers) with its SHA-256: the exporter's output changes between Blender releases, the script's
/// between converter versions, and the GLB with any file it was made from. The Blender addon reads
/// the same keys, so they are a cross-language contract.
/// </remarks>
public static class BlenderModelConverter
{
    public const string BlenderPathEnvironmentVariable = "PARADISE_BLENDER_PATH";

    /// <summary>Bumped whenever the script or its export settings change, so every GLB made by an earlier one converts again.</summary>
    public const int ConverterVersion = 2;

    internal const string SourceSha256Extra = "paradiseSourceSha256";
    internal const string ConverterVersionExtra = "paradiseConverterVersion";
    internal const string BlenderVersionExtra = "paradiseBlenderVersion";
    internal const string DependenciesExtra = "paradiseDependencies";

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

    private static readonly ConcurrentDictionary<string, string?> s_versions = new(StringComparer.Ordinal);

    /// <summary>One external file a conversion read: its path relative to the source's directory, <c>/</c>-separated, and its SHA-256.</summary>
    internal readonly record struct Dependency(string Path, string Sha256);

    /// <summary>What a converted GLB was made from.</summary>
    internal readonly record struct SourceStamp(string SourceSha256, int ConverterVersion, string BlenderVersion, IReadOnlyList<Dependency> Dependencies);

    /// <summary>What Blender exported, unstamped, and the files it read doing so, relative to the source's directory.</summary>
    internal readonly record struct Export(byte[] Glb, IReadOnlyList<string> Dependencies);

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

    /// <summary>The first non-empty line of <c>blender --version</c>, asked once per executable per process; null when it does not answer.</summary>
    public static string? BlenderVersion(string blenderPath)
    {
        ArgumentNullException.ThrowIfNull(blenderPath);
        return s_versions.GetOrAdd(blenderPath, static path =>
        {
            var run = ProcessTools.Run(path, "--version", timeoutMilliseconds: 60_000);
            if (!run.Succeeded) return null;
            return run.Stdout.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);
        });
    }

    /// <summary>
    /// Whether <paramref name="glb"/> still stands for a source with this hash: the source, the
    /// converter and every recorded dependency must match (<paramref name="dependencySha256"/>
    /// answers null for one that is gone), and so must the Blender version when there is a Blender
    /// to ask — with none, the GLB already made is the best there is.
    /// </summary>
    internal static bool IsCurrent(byte[] glb, string sourceSha256, string? blenderVersion, Func<string, string?> dependencySha256)
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
            && DependenciesMatch(extras, dependencySha256);
    }

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
        return GlbBinary.Write(gltf, bin);
    }

    /// <summary>Converts the source at <paramref name="sourceFullPath"/>; nothing is written beside the source.</summary>
    /// <exception cref="InvalidDataException">Blender failed, or exported nothing readable.</exception>
    internal static Export Convert(string blenderPath, string sourceFullPath)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "ParadiseModelConvert", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var script = Path.Combine(temporary, "to_glb.py");
            var staged = Path.Combine(temporary, "staged.glb");
            var dependencies = Path.Combine(temporary, "dependencies.json");
            File.WriteAllText(script, Script());

            // Without --python-exit-code a Python exception in the script exits 0.
            List<string> arguments = ["--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1"];

            // A .blend is opened as the main file (its embedded scripts are not the build's to run,
            // hence --disable-autoexec); every other format is imported by the script.
            if (string.Equals(Path.GetExtension(sourceFullPath), ".blend", StringComparison.OrdinalIgnoreCase))
            {
                arguments.Add(ProcessTools.QuoteArgument(sourceFullPath));
            }

            arguments.AddRange([
                "--python", ProcessTools.QuoteArgument(script), "--",
                ProcessTools.QuoteArgument(sourceFullPath), ProcessTools.QuoteArgument(staged), ProcessTools.QuoteArgument(dependencies),
            ]);

            var run = ProcessTools.Run(blenderPath, string.Join(' ', arguments), BlenderTimeoutMilliseconds);
            if (!run.Succeeded) throw new InvalidDataException(run.Describe($"Blender converting '{sourceFullPath}'", BlenderTimeoutMilliseconds));
            if (!File.Exists(staged) || !File.Exists(dependencies)) throw new InvalidDataException($"Blender exited 0 but exported no GLB for '{sourceFullPath}'.\n{run.Stdout}{run.Stderr}");

            var listed = JsonNode.Parse(File.ReadAllText(dependencies)) as JsonArray ?? [];
            return new Export(File.ReadAllBytes(staged), [.. listed.Select(node => node?.GetValue<string>()).OfType<string>()]);
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

    /// <summary>The conversion script: the import table as a dispatch dictionary, then the GLB export and the list of files the import read.</summary>
    private static string Script()
    {
        var importers = new StringBuilder();
        foreach (var (extension, import) in s_importers)
        {
            if (import is not null) importers.Append(CultureInfo.InvariantCulture, $"    '{extension}': lambda source: {import},\n");
        }

        return ScriptTemplate.Replace("#IMPORTERS#\n", importers.ToString(), StringComparison.Ordinal);
    }

    private const string ScriptTemplate = """
        import json
        import os
        import sys

        import bpy

        source, glb_out, dependencies_out = sys.argv[sys.argv.index('--') + 1:][:3]
        extension = os.path.splitext(source)[1].lower()

        IMPORTERS = {
        #IMPORTERS#
        }

        # A .blend arrives already open as the main file; everything else is imported into an empty scene.
        if extension in IMPORTERS:
            bpy.ops.wm.read_factory_settings(use_empty=True)
            IMPORTERS[extension](source)

        bpy.ops.export_scene.gltf(
            filepath=glb_out,
            export_format='GLB',
            export_yup=True,
            export_apply=True,
            export_animations=True,
            # Off by default; without them the runtime fills a constant tangent and normal maps shade wrong.
            export_tangents=True,
        )


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
