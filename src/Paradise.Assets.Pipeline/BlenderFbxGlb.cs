#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;



namespace Paradise.Assets.Pipeline
{
    /// <summary>FBX to GLB through headless Blender, skipped when the FBX hash stamped in the GLB's <c>asset.extras</c> matches. A Python failure exits 0 and a stale GLB then gets the new stamp — issue #205.</summary>
    public static class BlenderFbxGlb
    {
        public const string BlenderPathEnvironmentVariable = "PARADISE_BLENDER_PATH";
        private const string SourceFbxSha256ExtraName = "paradiseSourceFbxSha256";
        private const int BlenderTimeoutMilliseconds = 30 * 60 * 1000;

        public enum Result
        {
            UpToDate,
            Converted,
            ToolMissing,
            Failed,
        }

        public static Result Convert(
            string fbxFullPath,
            string glbFullPath,
            bool force = false,
            Action<string>? log = null,
            Action<string>? error = null)
        {
            string? blenderPath = FindBlender();
            if (string.IsNullOrWhiteSpace(blenderPath))
            {
                error?.Invoke(
                    $"Blender not found. Set {BlenderPathEnvironmentVariable} or install Blender to a standard location.");
                return Result.ToolMissing;
            }

            if (!File.Exists(fbxFullPath))
            {
                error?.Invoke($"FBX not found: '{fbxFullPath}'.");
                return Result.Failed;
            }

            string sourceHash = ProcessTools.ComputeFileSha256(fbxFullPath);
            if (!force && GeneratedGlbMatchesHash(glbFullPath, sourceHash))
            {
                log?.Invoke($"GLB '{glbFullPath}' is current for '{fbxFullPath}'; skipping.");
                return Result.UpToDate;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(glbFullPath)) ?? ".");
            string tempDirectory = Path.Combine(Path.GetTempPath(), "ParadiseFbx2Glb", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            string scriptPath = Path.Combine(tempDirectory, "fbx_to_glb.py");

            try
            {
                File.WriteAllText(scriptPath, BlenderFbxToGlbScript);
                string arguments = string.Join(
                    " ",
                    "--background",
                    "--factory-startup",
                    "--python",
                    ProcessTools.QuoteArgument(scriptPath),
                    "--",
                    ProcessTools.QuoteArgument(fbxFullPath),
                    ProcessTools.QuoteArgument(glbFullPath));

                ProcessTools.ProcessResult run = ProcessTools.Run(blenderPath, arguments, BlenderTimeoutMilliseconds);
                if (run.TimedOut)
                {
                    error?.Invoke($"Blender timed out converting '{fbxFullPath}'.\n{run.Stdout}{run.Stderr}");
                    return Result.Failed;
                }

                if (!run.Succeeded || !File.Exists(glbFullPath))
                {
                    error?.Invoke($"Blender failed (code {run.ExitCode}) converting '{fbxFullPath}'.\n{run.Stdout}{run.Stderr}");
                    return Result.Failed;
                }

                WriteGeneratedSourceHash(glbFullPath, sourceHash, error);
                log?.Invoke($"Converted '{fbxFullPath}' → '{glbFullPath}'.");
                return Result.Converted;
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        private const string BlenderFbxToGlbScript = @"
import bpy
import sys

argv = sys.argv
separator_index = argv.index('--')
fbx_in = argv[separator_index + 1]
glb_out = argv[separator_index + 2]

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=fbx_in, automatic_bone_orientation=True)
bpy.ops.export_scene.gltf(
    filepath=glb_out,
    export_format='GLB',
    export_yup=True,
    export_apply=True,
    export_animations=True,
)
";

        public static string? FindBlender() =>
            ProcessTools.FindExecutable(
                Environment.GetEnvironmentVariable(BlenderPathEnvironmentVariable),
                DefaultBlenderPaths(),
                "blender");

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
                foreach (string? programFiles in new[]
                         {
                             Environment.GetEnvironmentVariable("ProgramFiles"),
                             Environment.GetEnvironmentVariable("ProgramW6432"),
                         })
                {
                    if (string.IsNullOrWhiteSpace(programFiles))
                    {
                        continue;
                    }

                    string foundation = Path.Combine(programFiles, "Blender Foundation");
                    if (!Directory.Exists(foundation))
                    {
                        continue;
                    }

                    foreach (string candidate in Directory.EnumerateFiles(foundation, "blender.exe", SearchOption.AllDirectories))
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

        private static bool GeneratedGlbMatchesHash(string glbFullPath, string sourceHash)
        {
            if (!GlbBinary.TryRead(glbFullPath, out JsonObject gltf, out _))
            {
                return false;
            }

            string? storedHash = ((gltf["asset"] as JsonObject)?["extras"] as JsonObject)?[SourceFbxSha256ExtraName]?.GetValue<string>();
            return !string.IsNullOrWhiteSpace(storedHash) &&
                string.Equals(storedHash, sourceHash, StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteGeneratedSourceHash(string glbFullPath, string sourceHash, Action<string>? error)
        {
            if (!GlbBinary.TryRead(glbFullPath, out JsonObject gltf, out byte[] binChunk))
            {
                // Without the stamp every future run re-converts this asset; say so.
                error?.Invoke($"Could not re-read '{glbFullPath}' to write the source-FBX hash; it will be re-converted next run.");
                return;
            }

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

            extras[SourceFbxSha256ExtraName] = sourceHash;
            GlbBinary.Write(glbFullPath, gltf, binChunk);
        }
    }
}
