namespace Paradise.Rendering;

/// <summary>Creation parameters for a GPU sampler.</summary>
public readonly record struct SamplerDesc(
    string? Name,
    SamplerAddressMode AddressU,
    SamplerAddressMode AddressV,
    SamplerAddressMode AddressW,
    SamplerFilterMode MagFilter,
    SamplerFilterMode MinFilter,
    SamplerFilterMode MipmapFilter);
