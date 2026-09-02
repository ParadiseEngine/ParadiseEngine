using Paradise.Assets.Documents;
using Paradise.Assets.Project;

namespace Paradise.Assets.Pipeline;

/// <summary>The texture step's one external dependency, so the build is testable without <c>ktx</c>.</summary>
public interface ITextureEncoder
{
    /// <summary>Tool plus version, part of every cache key: a tool upgrade must miss the cache.</summary>
    string Identity { get; }

    /// <summary>The COMPLETE input of one encode, so the artifact cache can serve it; anything that would change the bytes out must change this. Only the encoder knows what its inputs are, which is why the step cannot compute it (issue #212).</summary>
    string CacheKey(byte[] source, string sourceExtension, TexturePreset preset, TextureQuality quality);

    bool TryEncode(byte[] source, string sourceExtension, TexturePreset preset, TextureQuality quality, out byte[] ktx2, out string error);
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
        var ktxPath = KtxTool.Find(repoRoot);
        if (string.IsNullOrWhiteSpace(ktxPath)) return false;

        var probe = KtxTool.Probe(ktxPath);
        if (!probe.Usable)
        {
            problem = probe.Problem;
            return false;
        }

        encoder = new KtxTextureEncoder(ktxPath, probe.VersionText!);
        return true;
    }

    /// <inheritdoc />
    public string CacheKey(byte[] source, string sourceExtension, TexturePreset preset, TextureQuality quality)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceExtension);

        // The argv is the policy, spelled out: a preset or quality change is a different argv.
        var argv = TextureEncodePolicy.CreateArguments(ToKtxPreset(preset), "out.ktx2", "in" + sourceExtension, quality);
        return ArtifactDigest.Compute(source, argv, Identity);
    }

    /// <inheritdoc />
    public bool TryEncode(byte[] source, string sourceExtension, TexturePreset preset, TextureQuality quality, out byte[] ktx2, out string error)
        => KtxTool.TryEncode(_ktxPath, source, sourceExtension, ToKtxPreset(preset), quality, out ktx2, out error);

    internal static TextureEncodingPreset ToKtxPreset(TexturePreset preset) => preset switch
    {
        TexturePreset.Color => TextureEncodingPreset.UastcColorSrgb,
        TexturePreset.ColorLinear => TextureEncodingPreset.UastcColorLinear,
        TexturePreset.Normal => TextureEncodingPreset.UastcNormalLinear,
        TexturePreset.Data => TextureEncodingPreset.UastcDataLinear,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown texture preset."),
    };

    internal static TexturePreset FromKtxPreset(TextureEncodingPreset preset) => preset switch
    {
        TextureEncodingPreset.UastcNormalLinear => TexturePreset.Normal,
        TextureEncodingPreset.UastcDataLinear => TexturePreset.Data,
        TextureEncodingPreset.UastcColorLinear => TexturePreset.ColorLinear,
        _ => TexturePreset.Color,
    };
}
