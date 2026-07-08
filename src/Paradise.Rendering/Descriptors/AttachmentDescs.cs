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
    ColorRgba ClearValue);

/// <summary>Depth attachment binding for a render pass.</summary>
// TODO(post-M0a): when the contract grows to express stencil load/store/clear, fold them in here
//                 (or split into DepthStencilAttachmentDesc) so combined formats like
//                 TextureFormat.Depth24PlusStencil8 round-trip without losing stencil intent.
//                 Tracked alongside PipelineDesc placeholder expansion in #42 / #45.
public readonly record struct DepthAttachmentDesc(
    TextureHandle DepthTexture,
    LoadOp DepthLoad,
    StoreOp DepthStore,
    float ClearDepth,
    // When valid, render into this specific view (e.g. one layer of a depth array) instead of the
    // texture's default view. DepthTexture is still used to identify the resource.
    TextureViewHandle DepthView = default);
