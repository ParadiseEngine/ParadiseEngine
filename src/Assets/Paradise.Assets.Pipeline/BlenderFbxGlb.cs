#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Paradise.Assets.Pipeline
{
    /// <summary>FBX to GLB through headless Blender, skipped when the stamp in the GLB's <c>asset.extras</c> (FBX hash and Blender version) matches.</summary>
    public static partial class BlenderFbxGlb
    {
        public const string BlenderPathEnvironmentVariable = "PARADISE_BLENDER_PATH";
        private const string SourceFbxSha256ExtraName = "paradiseSourceFbxSha256";
        private const string BlenderVersionExtraName = "paradiseBlenderVersion";
        private const int BlenderTimeoutMilliseconds = 30 * 60 * 1000;

        /// <summary>What the GLB was made from. The Blender version is part of it because the exporter's output changes between releases.</summary>
        internal readonly record struct SourceStamp(string FbxSha256, string BlenderVersion);

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
            ILogger? logger = null)
        {
            var log = logger ?? NullLogger.Instance;
            string? blenderPath = FindBlender();
            if (string.IsNullOrWhiteSpace(blenderPath))
            {
                LogBlenderMissing(log, BlenderPathEnvironmentVariable);
                return Result.ToolMissing;
            }

            if (!File.Exists(fbxFullPath))
            {
                LogFbxMissing(log, fbxFullPath);
                return Result.Failed;
            }

            string? blenderVersion = BlenderVersion(blenderPath);
            if (blenderVersion is null)
            {
                LogBlenderUnrunnable(log, blenderPath);
                return Result.ToolMissing;
            }

            var stamp = new SourceStamp(ProcessTools.ComputeFileSha256(fbxFullPath), blenderVersion);
            if (!force && GeneratedGlbMatchesStamp(glbFullPath, stamp))
            {
                LogGlbUpToDate(log, glbFullPath, fbxFullPath);
                return Result.UpToDate;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(glbFullPath)) ?? ".");
            string tempDirectory = Path.Combine(Path.GetTempPath(), "ParadiseFbx2Glb", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            string scriptPath = Path.Combine(tempDirectory, "fbx_to_glb.py");
            // Exported here and moved on success: a failed run must leave the previous GLB
            // alone, and must never see a leftover pass the existence check.
            string stagedGlbPath = Path.Combine(tempDirectory, "staged.glb");

            try
            {
                File.WriteAllText(scriptPath, BlenderFbxToGlbScript);
                string arguments = string.Join(
                    " ",
                    "--background",
                    "--factory-startup",
                    // Without this a Python exception in the script exits 0.
                    "--python-exit-code", "1",
                    "--python",
                    ProcessTools.QuoteArgument(scriptPath),
                    "--",
                    ProcessTools.QuoteArgument(fbxFullPath),
                    ProcessTools.QuoteArgument(stagedGlbPath));

                ProcessTools.ProcessResult run = ProcessTools.Run(blenderPath, arguments, BlenderTimeoutMilliseconds);
                if (!run.Succeeded)
                {
                    LogBlenderRunFailed(log, run.Describe($"Blender converting '{fbxFullPath}'", BlenderTimeoutMilliseconds));
                    return Result.Failed;
                }

                if (!File.Exists(stagedGlbPath))
                {
                    LogBlenderExportedNothing(log, fbxFullPath, run.Stdout + run.Stderr);
                    return Result.Failed;
                }

                if (!WriteSourceStamp(stagedGlbPath, stamp, log))
                {
                    return Result.Failed;
                }

                File.Move(stagedGlbPath, glbFullPath, overwrite: true);
                LogConverted(log, fbxFullPath, glbFullPath);
                return Result.Converted;
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        private static string? BlenderVersion(string blenderPath)
        {
            ProcessTools.ProcessResult run = ProcessTools.Run(blenderPath, "--version", timeoutMilliseconds: 60_000);
            if (!run.Succeeded)
            {
                return null;
            }

            string? first = run.Stdout.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);
            return string.IsNullOrWhiteSpace(first) ? null : first;
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
    # Off by default; without them the runtime fills a constant tangent and normal maps shade wrong.
    export_tangents=True,
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

        internal static bool GeneratedGlbMatchesStamp(string glbFullPath, SourceStamp stamp)
        {
            if (!File.Exists(glbFullPath) || !GlbBinary.TryRead(glbFullPath, out JsonObject gltf, out _))
            {
                return false;
            }

            var extras = (gltf["asset"] as JsonObject)?["extras"] as JsonObject;
            string? storedHash = extras?[SourceFbxSha256ExtraName]?.GetValue<string>();
            string? storedVersion = extras?[BlenderVersionExtraName]?.GetValue<string>();
            return !string.IsNullOrWhiteSpace(storedHash) &&
                string.Equals(storedHash, stamp.FbxSha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(storedVersion, stamp.BlenderVersion, StringComparison.Ordinal);
        }

        internal static bool WriteSourceStamp(string glbFullPath, SourceStamp stamp, ILogger log)
        {
            if (!GlbBinary.TryRead(glbFullPath, out JsonObject gltf, out byte[] binChunk))
            {
                LogUnreadableGlb(log, glbFullPath);
                return false;
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

            extras[SourceFbxSha256ExtraName] = stamp.FbxSha256;
            extras[BlenderVersionExtraName] = stamp.BlenderVersion;
            GlbBinary.Write(glbFullPath, gltf, binChunk);
            return true;
        }

        // These paths are host paths already — this drives an external Blender by absolute path
        // and never sees a mount — so no renderer is involved and they log as plain strings.

        [LoggerMessage(EventId = 40, Level = LogLevel.Error, Message = "Blender not found. Set {EnvironmentVariable} or install Blender to a standard location.")]
        private static partial void LogBlenderMissing(ILogger logger, string environmentVariable);

        [LoggerMessage(EventId = 41, Level = LogLevel.Error, Message = "FBX not found: '{FbxPath}'.")]
        private static partial void LogFbxMissing(ILogger logger, string fbxPath);

        [LoggerMessage(EventId = 42, Level = LogLevel.Error, Message = "Blender at '{BlenderPath}' did not answer '--version'; is it runnable?")]
        private static partial void LogBlenderUnrunnable(ILogger logger, string blenderPath);

        [LoggerMessage(EventId = 43, Level = LogLevel.Information, Message = "GLB '{GlbPath}' is current for '{FbxPath}'; skipping.")]
        private static partial void LogGlbUpToDate(ILogger logger, string glbPath, string fbxPath);

        [LoggerMessage(EventId = 44, Level = LogLevel.Error, Message = "{Report}")]
        private static partial void LogBlenderRunFailed(ILogger logger, string report);

        [LoggerMessage(EventId = 45, Level = LogLevel.Error, Message = "Blender exited 0 but exported no GLB for '{FbxPath}'.\n{Output}")]
        private static partial void LogBlenderExportedNothing(ILogger logger, string fbxPath, string output);

        [LoggerMessage(EventId = 46, Level = LogLevel.Information, Message = "Converted '{FbxPath}' → '{GlbPath}'.")]
        private static partial void LogConverted(ILogger logger, string fbxPath, string glbPath);

        [LoggerMessage(EventId = 47, Level = LogLevel.Error, Message = "Blender's export '{GlbPath}' is not a readable GLB.")]
        private static partial void LogUnreadableGlb(ILogger logger, string glbPath);
    }
}
