using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Paradise.Assets.Pipeline;

/// <summary>A <c>.blend</c> or <c>.fbx</c> to GLB through headless Blender, stamped in the GLB's <c>asset.extras</c> with what it was made from.</summary>
/// <remarks>
/// The stamp is the source's SHA-256, <see cref="ConverterVersion"/> and the Blender version: the
/// exporter's output changes between Blender releases, and the script's between converter
/// versions. The Blender addon reads the same three keys, so they are a cross-language contract.
/// </remarks>
public static class BlenderModelConverter
{
    public const string BlenderPathEnvironmentVariable = "PARADISE_BLENDER_PATH";

    /// <summary>Bumped whenever the script or its export settings change, so every GLB made by an earlier one converts again.</summary>
    public const int ConverterVersion = 1;

    internal const string SourceSha256Extra = "paradiseSourceSha256";
    internal const string ConverterVersionExtra = "paradiseConverterVersion";
    internal const string BlenderVersionExtra = "paradiseBlenderVersion";

    private const int BlenderTimeoutMilliseconds = 30 * 60 * 1000;

    private static readonly ConcurrentDictionary<string, string?> s_versions = new(StringComparer.Ordinal);

    /// <summary>What a converted GLB was made from.</summary>
    internal readonly record struct SourceStamp(string SourceSha256, int ConverterVersion, string BlenderVersion);

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
    /// Whether <paramref name="glb"/> still stands for a source with this hash: the source and the
    /// converter must match, and so must the Blender version when there is a Blender to ask — with
    /// none, the GLB already made is the best there is.
    /// </summary>
    internal static bool IsCurrent(byte[] glb, string sourceSha256, string? blenderVersion)
    {
        if (!GlbBinary.TryRead(glb, out var gltf, out _)) return false;
        if ((gltf["asset"] as JsonObject)?["extras"] is not JsonObject extras) return false;

        return extras[SourceSha256Extra] is JsonValue sha && sha.TryGetValue(out string? storedSha)
            && string.Equals(storedSha, sourceSha256, StringComparison.OrdinalIgnoreCase)
            && extras[ConverterVersionExtra] is JsonValue converter && converter.TryGetValue(out int storedConverter)
            && storedConverter == ConverterVersion
            && (blenderVersion is null
                || (extras[BlenderVersionExtra] is JsonValue blender && blender.TryGetValue(out string? storedBlender)
                    && string.Equals(storedBlender, blenderVersion, StringComparison.Ordinal)));
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
        return GlbBinary.Write(gltf, bin);
    }

    /// <summary>Converts the source at <paramref name="sourceFullPath"/> and returns the stamped GLB; nothing is written beside the source.</summary>
    /// <exception cref="InvalidDataException">Blender failed, or exported nothing readable.</exception>
    internal static byte[] Convert(string blenderPath, string sourceFullPath, SourceStamp stamp)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "ParadiseModelConvert", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var script = Path.Combine(temporary, "to_glb.py");
            var staged = Path.Combine(temporary, "staged.glb");
            File.WriteAllText(script, Script);

            // Without --python-exit-code a Python exception in the script exits 0.
            List<string> arguments = ["--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1"];

            // A .blend is opened as the main file (its embedded scripts are not the build's to run,
            // hence --disable-autoexec); an FBX is imported by the script.
            if (string.Equals(Path.GetExtension(sourceFullPath), ".blend", StringComparison.OrdinalIgnoreCase))
            {
                arguments.Add(ProcessTools.QuoteArgument(sourceFullPath));
            }

            arguments.AddRange(["--python", ProcessTools.QuoteArgument(script), "--", ProcessTools.QuoteArgument(sourceFullPath), ProcessTools.QuoteArgument(staged)]);

            var run = ProcessTools.Run(blenderPath, string.Join(' ', arguments), BlenderTimeoutMilliseconds);
            if (!run.Succeeded) throw new InvalidDataException(run.Describe($"Blender converting '{sourceFullPath}'", BlenderTimeoutMilliseconds));
            if (!File.Exists(staged)) throw new InvalidDataException($"Blender exited 0 but exported no GLB for '{sourceFullPath}'.\n{run.Stdout}{run.Stderr}");

            return Stamp(File.ReadAllBytes(staged), stamp);
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

    private const string Script = """
        import sys

        import bpy

        source, glb_out = sys.argv[sys.argv.index('--') + 1:][:2]

        # A .blend arrives already open as the main file; an FBX is imported into an empty scene.
        if source.lower().endswith('.fbx'):
            bpy.ops.wm.read_factory_settings(use_empty=True)
            bpy.ops.import_scene.fbx(filepath=source, automatic_bone_orientation=True)

        bpy.ops.export_scene.gltf(
            filepath=glb_out,
            export_format='GLB',
            export_yup=True,
            export_apply=True,
            export_animations=True,
            # Off by default; without them the runtime fills a constant tangent and normal maps shade wrong.
            export_tangents=True,
        )
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
