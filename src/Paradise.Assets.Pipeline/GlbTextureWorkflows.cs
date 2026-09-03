using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paradise.Assets.Project;

namespace Paradise.Assets.Pipeline;

public enum ConversionResult
{
    NoConvertibleTextures,
    ConvertedAllTextures,
    ToolMissing,
    Failed,
}

/// <summary>
/// The path-based texture workflows of the editor hosts (Godot's data/ converter): encode a
/// standalone image, embed a GLB's textures as KTX2, or externalise them. Thin: tool resolution
/// is <see cref="KtxTool"/>'s, the rewrite is <see cref="GlbTextureRewriter"/>'s, and every
/// output lands by temp-then-rename so a killed run leaves the input GLB whole.
/// </summary>
public static partial class GlbTextureWorkflows
{
    /// <summary>Encodes a standalone image to a KTX2 beside it; skipped by timestamp when the output is newer.</summary>
    public static ConversionResult ConvertImageFile(
        string sourceFullPath,
        string outputKtx2Path,
        string? repoRoot = null,
        ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        ArgumentNullException.ThrowIfNull(sourceFullPath);
        ArgumentNullException.ThrowIfNull(outputKtx2Path);

        if (!File.Exists(sourceFullPath))
        {
            LogSourceImageMissing(log, sourceFullPath);
            return ConversionResult.Failed;
        }

        if (File.Exists(outputKtx2Path) && File.GetLastWriteTimeUtc(outputKtx2Path) >= File.GetLastWriteTimeUtc(sourceFullPath))
        {
            return ConversionResult.NoConvertibleTextures;
        }

        var ktxPath = KtxTool.Find(repoRoot);
        if (ktxPath is null)
        {
            LogToolMissing(log, ToolMissingMessage);
            return ConversionResult.ToolMissing;
        }

        var extension = Path.GetExtension(sourceFullPath).ToLowerInvariant() is ".jpg" or ".jpeg" ? ".jpg" : ".png";
        if (!KtxTool.TryEncode(ktxPath, File.ReadAllBytes(sourceFullPath), extension, TextureEncodingPreset.UastcColorSrgb, TextureQuality.Full, out var ktx2, out var problem))
        {
            LogProblem(log, problem);
            return ConversionResult.Failed;
        }

        AtomicFile.Write(outputKtx2Path, ktx2);
        LogKtx2Image(log, Path.GetFileName(sourceFullPath), Path.GetFileName(outputKtx2Path), ktx2.Length);
        return ConversionResult.ConvertedAllTextures;
    }

    /// <summary>Replaces every embedded PNG/JPEG with KTX2 in place; a GLB whose images are already KTX2 or external is left alone.</summary>
    public static ConversionResult ConvertEmbeddedTextures(
        string glbFullPath,
        string? repoRoot = null,
        ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        ArgumentNullException.ThrowIfNull(glbFullPath);

        if (!TryReadGlb(glbFullPath, out var glb, out var images, log)) return ConversionResult.Failed;

        var convertible = images.Where(image => !image.IsKtx2).ToList();
        if (convertible.Count == 0) return ConversionResult.NoConvertibleTextures;

        var ktxPath = KtxTool.Find(repoRoot);
        if (ktxPath is null)
        {
            LogToolMissing(log, ToolMissingMessage);
            return ConversionResult.ToolMissing;
        }

        var ktx2ByImage = new Dictionary<int, byte[]>();
        foreach (var image in convertible)
        {
            if (image.PresetNote is { } note) LogPresetNote(log, note);
            if (!KtxTool.TryEncode(ktxPath, image.Bytes, image.SourceExtension!, image.Preset, TextureQuality.Full, out var ktx2, out var problem))
            {
                LogProblem(log, problem);
                continue;
            }

            ktx2ByImage[image.Index] = ktx2;
        }

        if (ktx2ByImage.Count != convertible.Count)
        {
            LogPartialConversion(log, ktx2ByImage.Count, convertible.Count, glbFullPath);
            return ConversionResult.Failed;
        }

        if (!GlbTextureRewriter.TryEmbedKtx2(glb, ktx2ByImage, out var rewritten, out var rewriteError))
        {
            LogGlbProblem(log, glbFullPath, rewriteError);
            return ConversionResult.Failed;
        }

        AtomicFile.Write(glbFullPath, rewritten);
        LogConvertedEmbedded(log, ktx2ByImage.Count, glbFullPath);
        return ConversionResult.ConvertedAllTextures;
    }

    /// <summary>Rewrites a GLB so every texture is an external <c>&lt;stem&gt;_&lt;i&gt;.ktx2</c> sidecar and the BIN chunk holds geometry only; idempotent.</summary>
    public static ConversionResult ExternalizeTextures(
        string glbFullPath,
        string? repoRoot = null,
        ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        ArgumentNullException.ThrowIfNull(glbFullPath);

        if (!TryReadGlb(glbFullPath, out var glb, out var images, log)) return ConversionResult.Failed;
        if (images.Count == 0) return ConversionResult.NoConvertibleTextures;

        var directory = Path.GetDirectoryName(glbFullPath) ?? ".";
        var sidecars = new List<(string Name, byte[] Bytes)>();
        string? ktxPath = null;
        foreach (var image in images)
        {
            if (image.IsKtx2)
            {
                // Pre-encoded KTX2 still gets the project's LINEAR tag, for the Godot
                // double-decode reason in TextureEncodePolicy.CreateArguments.
                Ktx2Header.ForceLinearTransfer(image.Bytes);
                sidecars.Add((image.SidecarName, image.Bytes));
                continue;
            }

            ktxPath ??= KtxTool.Find(repoRoot);
            if (ktxPath is null)
            {
                LogToolMissing(log, ToolMissingMessage);
                return ConversionResult.ToolMissing;
            }

            if (image.PresetNote is { } note) LogPresetNote(log, note);
            if (!KtxTool.TryEncode(ktxPath, image.Bytes, image.SourceExtension!, image.Preset, TextureQuality.Full, out var ktx2, out var problem))
            {
                LogProblem(log, problem);
                return ConversionResult.Failed;
            }

            sidecars.Add((image.SidecarName, ktx2));
        }

        if (!GlbTextureRewriter.TryExternalize(glb, images, out var rewritten, out var rewriteError))
        {
            LogGlbProblem(log, glbFullPath, rewriteError);
            return ConversionResult.Failed;
        }

        foreach (var (name, bytes) in sidecars) AtomicFile.Write(Path.Combine(directory, name), bytes);
        AtomicFile.Write(glbFullPath, rewritten);
        LogExternalized(log, sidecars.Count, glbFullPath);
        return ConversionResult.ConvertedAllTextures;
    }

    private static bool TryReadGlb(string glbFullPath, out byte[] glb, out IReadOnlyList<EmbeddedImage> images, ILogger log)
    {
        glb = [];
        images = [];
        if (!File.Exists(glbFullPath))
        {
            LogUnparseableGlb(log, glbFullPath);
            return false;
        }

        glb = File.ReadAllBytes(glbFullPath);
        if (GlbTextureRewriter.TryListEmbedded(glb, Path.GetFileNameWithoutExtension(glbFullPath), out images, out var problem)) return true;

        LogGlbProblem(log, glbFullPath, problem);
        return false;
    }

    private static string ToolMissingMessage =>
        $"ktx not found. Set {KtxTool.PathEnvironmentVariable}, vendor KTX-Software v5 under third_party/tools/KTX-Software, or add ktx to PATH.";

    // Host paths throughout: these drive the external `ktx` tool, which takes no mount.

    [LoggerMessage(EventId = 50, Level = LogLevel.Error, Message = "Source image not found: '{SourcePath}'.")]
    private static partial void LogSourceImageMissing(ILogger logger, string sourcePath);

    [LoggerMessage(EventId = 51, Level = LogLevel.Error, Message = "{Problem}")]
    private static partial void LogToolMissing(ILogger logger, string problem);

    [LoggerMessage(EventId = 52, Level = LogLevel.Error, Message = "{Problem}")]
    private static partial void LogProblem(ILogger logger, string problem);

    [LoggerMessage(EventId = 53, Level = LogLevel.Information, Message = "KTX2 image: {Source} → {Output} ({Bytes} bytes)")]
    private static partial void LogKtx2Image(ILogger logger, string source, string output, int bytes);

    [LoggerMessage(EventId = 54, Level = LogLevel.Information, Message = "{Note}")]
    private static partial void LogPresetNote(ILogger logger, string note);

    [LoggerMessage(EventId = 55, Level = LogLevel.Error, Message = "Converted {Converted} of {Total} textures in '{GlbPath}'; GLB not rewritten.")]
    private static partial void LogPartialConversion(ILogger logger, int converted, int total, string glbPath);

    [LoggerMessage(EventId = 56, Level = LogLevel.Error, Message = "'{GlbPath}' {Problem}.")]
    private static partial void LogGlbProblem(ILogger logger, string glbPath, string problem);

    [LoggerMessage(EventId = 57, Level = LogLevel.Information, Message = "Converted {Count} embedded texture(s) in '{GlbPath}' to KTX2.")]
    private static partial void LogConvertedEmbedded(ILogger logger, int count, string glbPath);

    [LoggerMessage(EventId = 58, Level = LogLevel.Information, Message = "Externalized {Count} texture(s) from '{GlbPath}' to sidecar .ktx2 file(s).")]
    private static partial void LogExternalized(ILogger logger, int count, string glbPath);

    [LoggerMessage(EventId = 59, Level = LogLevel.Error, Message = "Failed to parse GLB '{GlbPath}'.")]
    private static partial void LogUnparseableGlb(ILogger logger, string glbPath);
}

/// <summary>Writes a whole file or none of it: a killed process must not leave a half-written GLB where a whole one was.</summary>
internal static class AtomicFile
{
    public static void Write(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
