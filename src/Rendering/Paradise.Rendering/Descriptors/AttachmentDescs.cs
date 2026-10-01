using System.Runtime.InteropServices;

namespace Paradise.Rendering;

/// <summary>Color attachment binding for a single render-pass color slot.</summary>
/// <remarks>Sequential layout is required: <see cref="ColorAttachmentBuffer"/> stores instances
/// inline and <see cref="RenderPassDesc"/> walks them via <see cref="System.Runtime.CompilerServices.Unsafe.Add{T}(ref T, int)"/>.</remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ColorAttachmentDesc(
    RenderViewHandle View,
    LoadOp Load,
    StoreOp Store,
    ColorRgba ClearValue,
    // When valid, render into this offscreen texture view instead of the backbuffer. The RenderView
    // above (typically Invalid → backbuffer) is ignored when this is set. Used by offscreen targets
    // such as the SSAO position pre-pass.
    TextureViewHandle ColorView = default);

/// <summary>Depth attachment binding for a render pass.</summary>
/// <remarks>Stencil load, store and clear operations are not represented, even when the texture
/// uses a combined depth/stencil format. Supporting them requires extending this contract.</remarks>
public readonly record struct DepthAttachmentDesc(
    TextureHandle DepthTexture,
    LoadOp DepthLoad,
    StoreOp DepthStore,
    float ClearDepth,
    // When valid, render into this specific view (e.g. one layer of a depth array) instead of the
    // texture's default view. DepthTexture is still used to identify the resource.
    TextureViewHandle DepthView = default);
