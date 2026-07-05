namespace Paradise.Rendering;

/// <summary>Creation parameters for a GPU sampler. <see cref="MaxAnisotropy"/> above 1 enables
/// anisotropic filtering (WebGPU requires all three filters to be Linear then).</summary>
public readonly record struct SamplerDesc(
    string? Name,
    SamplerAddressMode AddressU,
    SamplerAddressMode AddressV,
    SamplerAddressMode AddressW,
    SamplerFilterMode MagFilter,
    SamplerFilterMode MinFilter,
    SamplerFilterMode MipmapFilter,
    ushort MaxAnisotropy = 1);
