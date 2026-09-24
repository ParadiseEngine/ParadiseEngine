using System.Runtime.CompilerServices;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Owns neutral bindings for frame results whose producers are absent or disabled.</summary>
internal sealed class PbrFallbackResources : IDisposable
{
    private const string ShadowDepth = "Pbr.FallbackShadowDepth";
    private const ulong ZeroBytes = 8192;
    private readonly IRenderer _renderer;
    private readonly GraphTextureRegistry _targets;
    private readonly BufferHandle _zero;
    private readonly SamplerHandle _comparison;
    private readonly SamplerHandle _linear;

    public PbrFallbackResources(IRenderer renderer, GraphTextureRegistry targets)
    {
        _renderer = renderer;
        _targets = targets;
        _zero = renderer.CreateBuffer(new BufferDesc("PbrFallbackZero", ZeroBytes,
            BufferUsage.Uniform | BufferUsage.Storage | BufferUsage.CopyDst));
        renderer.UpdateBuffer<byte>(_zero, 0, new byte[ZeroBytes]);
        _comparison = renderer.CreateSampler(new SamplerDesc("PbrFallbackShadowSampler",
            SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge,
            SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Nearest, Compare: CompareFunction.LessEqual));
        _linear = renderer.CreateSampler(new SamplerDesc("PbrFallbackSampler",
            SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge,
            SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Nearest));
        targets.Ensure(ShadowDepth, new TextureDesc(null, 1, 1, 1, 1, 1, TextureDimension.D2,
            TextureFormat.Depth32Float, TextureUsage.TextureBinding | TextureUsage.RenderAttachment));
    }

    public ShadowFrameData Shadows(FrameGraph graph) =>
        new(graph.Texture(ShadowDepth), _comparison, 1, 0, 0, [], default, default, default);

    public LightGridFrameData LightGrid(FrameGraph graph) =>
        new(graph.ImportBuffer(_zero), 8, 0, 0, 0, 0.05f, 100f);

    public ProbeFrameData Probes() => new(_zero, 144, _zero, 16, _linear, 0, _zero, 8);

    public DecalFrameData Decals() => new(_zero, 16, _zero,
        (ulong)(PbrDecals.MaxDecals * Unsafe.SizeOf<DecalGpu>()), _targets.ArrayView(_targets.Black), _linear);

    public BufferHandle Ssao => _zero;

    public void Dispose()
    {
        _renderer.DestroyBuffer(_zero);
        _renderer.DestroySampler(_comparison);
        _renderer.DestroySampler(_linear);
    }
}
