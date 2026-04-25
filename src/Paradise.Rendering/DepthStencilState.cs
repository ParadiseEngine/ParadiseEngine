namespace Paradise.Rendering;

/// <summary>Depth/stencil pipeline state. M2 wires depth testing end-to-end (format, write-enable,
/// compare function). Full stencil state is reserved for a later milestone — <see cref="StencilReadMask"/>
/// and <see cref="StencilWriteMask"/> are carried so the contract is stable, but the backend defaults
/// stencil face state to "always pass, keep" until stencil authoring lands.</summary>
public readonly record struct DepthStencilState(
    TextureFormat Format,
    bool DepthWriteEnabled,
    CompareFunction DepthCompare,
    uint StencilReadMask = 0xFFFFFFFF,
    uint StencilWriteMask = 0xFFFFFFFF);
