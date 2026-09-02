using Paradise.Assets.Documents;

namespace Paradise.Assets.Pipeline;

/// <summary>The texture step's one external dependency, so the build is testable without <c>ktx</c>.</summary>
public interface ITextureEncoder
{
    /// <summary>Tool plus version, part of every cache key: a tool upgrade must miss the cache.</summary>
    string Identity { get; }

    bool TryEncode(byte[] source, string sourceExtension, TexturePreset preset, bool fastEncode, out byte[] ktx2, out string error);
}

public sealed class KtxTextureEncoder : ITextureEncoder
{
    private readonly string _ktxPath;

    private KtxTextureEncoder(string ktxPath, string identity)
    {
        _ktxPath = ktxPath;
        Identity = identity;
    }

    /// <inheritdoc />
    public string Identity { get; }

    /// <summary>
    /// <paramref name="problem"/> is null when no ktx was found at all (the texture step says so per
    /// asset) and names the fault when one was found but cannot serve: not runnable, or too old.
    /// </summary>
    public static bool TryCreate(string? repoRoot, out KtxTextureEncoder? encoder, out string? problem)
    {
        encoder = null;
        problem = null;
        var ktxPath = KtxCreate.FindKtx(repoRoot);
        if (string.IsNullOrWhiteSpace(ktxPath)) return false;

        var probe = KtxCreate.ProbeKtx(ktxPath);
        if (!probe.Usable)
        {
            problem = probe.Problem;
            return false;
        }

        encoder = new KtxTextureEncoder(ktxPath, probe.VersionText!);
        return true;
    }

    /// <inheritdoc />
    public bool TryEncode(byte[] source, string sourceExtension, TexturePreset preset, bool fastEncode, out byte[] ktx2, out string error)
    {
        var captured = "";
        var ok = KtxCreate.TryConvertImageBytes(
            _ktxPath, source, sourceExtension, ToKtxPreset(preset), fastEncode,
            out ktx2, message => captured = message);
        error = ok ? "" : captured;
        return ok;
    }

    internal static KtxCreate.TextureEncodingPreset ToKtxPreset(TexturePreset preset) => preset switch
    {
        TexturePreset.Color => KtxCreate.TextureEncodingPreset.UastcColorSrgb,
        TexturePreset.ColorLinear => KtxCreate.TextureEncodingPreset.UastcColorLinear,
        TexturePreset.Normal => KtxCreate.TextureEncodingPreset.UastcNormalLinear,
        TexturePreset.Data => KtxCreate.TextureEncodingPreset.UastcDataLinear,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown texture preset."),
    };
}
