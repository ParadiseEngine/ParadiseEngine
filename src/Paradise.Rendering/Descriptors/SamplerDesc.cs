namespace Paradise.Rendering;

/// <summary>Creation parameters for a GPU sampler.</summary>
public readonly struct SamplerDesc
{
    public readonly string? Name;
    public readonly SamplerAddressMode AddressU;
    public readonly SamplerAddressMode AddressV;
    public readonly SamplerAddressMode AddressW;
    public readonly SamplerFilterMode MagFilter;
    public readonly SamplerFilterMode MinFilter;
    public readonly SamplerFilterMode MipmapFilter;

    public SamplerDesc(
        string? name,
        SamplerAddressMode addressU,
        SamplerAddressMode addressV,
        SamplerAddressMode addressW,
        SamplerFilterMode magFilter,
        SamplerFilterMode minFilter,
        SamplerFilterMode mipmapFilter)
    {
        Name = name;
        AddressU = addressU;
        AddressV = addressV;
        AddressW = addressW;
        MagFilter = magFilter;
        MinFilter = minFilter;
        MipmapFilter = mipmapFilter;
    }
}
