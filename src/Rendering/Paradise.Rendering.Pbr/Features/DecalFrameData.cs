using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Publishes the ordered decal volumes and texture array used by the current frame.</summary>
public readonly record struct DecalFrameData(
    BufferHandle UniformBuffer, ulong UniformBufferBytes,
    BufferHandle DecalBuffer, ulong DecalBufferBytes,
    TextureViewHandle TextureView, SamplerHandle Sampler)
{
    public static readonly FrameDataKey<DecalFrameData> Key = new("Pbr.Decals");
}
