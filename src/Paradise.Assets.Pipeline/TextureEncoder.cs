using Paradise.Assets.Documents;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// The texture step's one external dependency, behind an interface so the build is testable
/// without the <c>ktx</c> CLI — and so the cache key can name the encoder.
/// </summary>
public interface ITextureEncoder
{
    /// <summary>
    /// A string identifying this encoder's behaviour — tool plus version. Part of every cache
    /// key: a tool upgrade must miss the cache, or last release's artifacts survive it.
    /// </summary>
    string Identity { get; }

    /// <summary>Encodes one source image to KTX2.</summary>
    /// <param name="source">The source image bytes (PNG/JPEG).</param>
    /// <param name="sourceExtension">The source's extension, with dot.</param>
    /// <param name="preset">The encoding preset.</param>
    /// <param name="fastEncode">The iteration-speed knob (<c>texture_quality = "fast"</c>).</param>
    /// <param name="ktx2">The encoded KTX2 bytes.</param>
    /// <param name="error">What went wrong when returning <see langword="false"/>.</param>
    bool TryEncode(byte[] source, string sourceExtension, TexturePreset preset, bool fastEncode, out byte[] ktx2, out string error);
}

/// <summary>
/// The real encoder: the Khronos <c>ktx create</c> CLI via <see cref="KtxCreate"/>.
/// </summary>
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
    /// Resolves <c>ktx</c> (environment override, a vendored <c>third_party/tools/KTX-Software</c>
    /// under <paramref name="repoRoot"/>, or PATH) and reads its version for the cache identity.
    /// </summary>
    /// <param name="repoRoot">The directory whose <c>third_party</c> may vendor KTX-Software.</param>
    /// <param name="encoder">The encoder, when the tool was found.</param>
    public static bool TryCreate(string? repoRoot, out KtxTextureEncoder? encoder)
    {
        encoder = null;
        var ktxPath = KtxCreate.FindKtx(repoRoot);
        if (string.IsNullOrWhiteSpace(ktxPath)) return false;

        // `ktx --version` prints e.g. "ktx 4.4.2". Fall back to the resolved path: a worse
        // identity than a version, but still one that changes when the install does.
        var probe = ProcessTools.Run(ktxPath, "--version", timeoutMilliseconds: 30_000);
        var identity = probe.Succeeded && probe.Stdout.Trim().Length > 0 ? probe.Stdout.Trim() : $"ktx@{ktxPath}";
        encoder = new KtxTextureEncoder(ktxPath, identity);
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

    /// <summary>The sidecar preset vocabulary mapped onto the encoder's.</summary>
    internal static KtxCreate.TextureEncodingPreset ToKtxPreset(TexturePreset preset) => preset switch
    {
        TexturePreset.Color => KtxCreate.TextureEncodingPreset.UastcColorSrgb,
        TexturePreset.ColorLinear => KtxCreate.TextureEncodingPreset.UastcColorLinear,
        TexturePreset.Normal => KtxCreate.TextureEncodingPreset.UastcNormalLinear,
        TexturePreset.Data => KtxCreate.TextureEncodingPreset.UastcDataLinear,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown texture preset."),
    };
}
