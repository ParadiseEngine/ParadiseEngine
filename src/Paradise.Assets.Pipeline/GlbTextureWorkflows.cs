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
public static class GlbTextureWorkflows
{
    /// <summary>Encodes a standalone image to a KTX2 beside it; skipped by timestamp when the output is newer.</summary>
    public static ConversionResult ConvertImageFile(
        string sourceFullPath,
        string outputKtx2Path,
        string? repoRoot = null,
        Action<string>? log = null,
        Action<string>? error = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFullPath);
        ArgumentNullException.ThrowIfNull(outputKtx2Path);

        if (!File.Exists(sourceFullPath))
        {
            error?.Invoke($"Source image not found: '{sourceFullPath}'.");
            return ConversionResult.Failed;
        }

        if (File.Exists(outputKtx2Path) && File.GetLastWriteTimeUtc(outputKtx2Path) >= File.GetLastWriteTimeUtc(sourceFullPath))
        {
            return ConversionResult.NoConvertibleTextures;
        }

        var ktxPath = KtxTool.Find(repoRoot);
        if (ktxPath is null)
        {
            error?.Invoke(ToolMissingMessage);
            return ConversionResult.ToolMissing;
        }

        var extension = Path.GetExtension(sourceFullPath).ToLowerInvariant() is ".jpg" or ".jpeg" ? ".jpg" : ".png";
        if (!KtxTool.TryEncode(ktxPath, File.ReadAllBytes(sourceFullPath), extension, TextureEncodingPreset.UastcColorSrgb, TextureQuality.Full, out var ktx2, out var problem))
        {
            error?.Invoke(problem);
            return ConversionResult.Failed;
        }

        AtomicFile.Write(outputKtx2Path, ktx2);
        log?.Invoke($"KTX2 image: {Path.GetFileName(sourceFullPath)} → {Path.GetFileName(outputKtx2Path)} ({ktx2.Length} bytes)");
        return ConversionResult.ConvertedAllTextures;
    }

    /// <summary>Replaces every embedded PNG/JPEG with KTX2 in place; a GLB whose images are already KTX2 or external is left alone.</summary>
    public static ConversionResult ConvertEmbeddedTextures(
        string glbFullPath,
        string? repoRoot = null,
        Action<string>? log = null,
        Action<string>? error = null)
    {
        ArgumentNullException.ThrowIfNull(glbFullPath);

        if (!TryReadGlb(glbFullPath, out var glb, out var images, error)) return ConversionResult.Failed;

        var convertible = images.Where(image => !image.IsKtx2).ToList();
        if (convertible.Count == 0) return ConversionResult.NoConvertibleTextures;

        var ktxPath = KtxTool.Find(repoRoot);
        if (ktxPath is null)
        {
            error?.Invoke(ToolMissingMessage);
            return ConversionResult.ToolMissing;
        }

        var ktx2ByImage = new Dictionary<int, byte[]>();
        foreach (var image in convertible)
        {
            if (image.PresetNote is { } note) log?.Invoke(note);
            if (!KtxTool.TryEncode(ktxPath, image.Bytes, image.SourceExtension!, image.Preset, TextureQuality.Full, out var ktx2, out var problem))
            {
                error?.Invoke(problem);
                continue;
            }

            ktx2ByImage[image.Index] = ktx2;
        }

        if (ktx2ByImage.Count != convertible.Count)
        {
            error?.Invoke($"Converted {ktx2ByImage.Count} of {convertible.Count} textures in '{glbFullPath}'; GLB not rewritten.");
            return ConversionResult.Failed;
        }

        if (!GlbTextureRewriter.TryEmbedKtx2(glb, ktx2ByImage, out var rewritten, out var rewriteError))
        {
            error?.Invoke($"'{glbFullPath}' {rewriteError}.");
            return ConversionResult.Failed;
        }

        AtomicFile.Write(glbFullPath, rewritten);
        log?.Invoke($"Converted {ktx2ByImage.Count} embedded texture(s) in '{glbFullPath}' to KTX2.");
        return ConversionResult.ConvertedAllTextures;
    }

    /// <summary>Rewrites a GLB so every texture is an external <c>&lt;stem&gt;_&lt;i&gt;.ktx2</c> sidecar and the BIN chunk holds geometry only; idempotent.</summary>
    public static ConversionResult ExternalizeTextures(
        string glbFullPath,
        string? repoRoot = null,
        Action<string>? log = null,
        Action<string>? error = null)
    {
        ArgumentNullException.ThrowIfNull(glbFullPath);

        if (!TryReadGlb(glbFullPath, out var glb, out var images, error)) return ConversionResult.Failed;
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
                error?.Invoke(ToolMissingMessage);
                return ConversionResult.ToolMissing;
            }

            if (image.PresetNote is { } note) log?.Invoke(note);
            if (!KtxTool.TryEncode(ktxPath, image.Bytes, image.SourceExtension!, image.Preset, TextureQuality.Full, out var ktx2, out var problem))
            {
                error?.Invoke(problem);
                return ConversionResult.Failed;
            }

            sidecars.Add((image.SidecarName, ktx2));
        }

        if (!GlbTextureRewriter.TryExternalize(glb, images, out var rewritten, out var rewriteError))
        {
            error?.Invoke($"'{glbFullPath}' {rewriteError}.");
            return ConversionResult.Failed;
        }

        foreach (var (name, bytes) in sidecars) AtomicFile.Write(Path.Combine(directory, name), bytes);
        AtomicFile.Write(glbFullPath, rewritten);
        log?.Invoke($"Externalized {sidecars.Count} texture(s) from '{glbFullPath}' to sidecar .ktx2 file(s).");
        return ConversionResult.ConvertedAllTextures;
    }

    private static bool TryReadGlb(string glbFullPath, out byte[] glb, out IReadOnlyList<EmbeddedImage> images, Action<string>? error)
    {
        glb = [];
        images = [];
        if (!File.Exists(glbFullPath))
        {
            error?.Invoke($"Failed to parse GLB '{glbFullPath}'.");
            return false;
        }

        glb = File.ReadAllBytes(glbFullPath);
        if (GlbTextureRewriter.TryListEmbedded(glb, Path.GetFileNameWithoutExtension(glbFullPath), out images, out var problem)) return true;

        error?.Invoke($"'{glbFullPath}' {problem}.");
        return false;
    }

    private static string ToolMissingMessage =>
        $"ktx not found. Set {KtxTool.PathEnvironmentVariable}, vendor KTX-Software v5 under third_party/tools/KTX-Software, or add ktx to PATH.";
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
