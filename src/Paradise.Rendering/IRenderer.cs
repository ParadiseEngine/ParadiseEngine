using System;

namespace Paradise.Rendering;

/// <summary>The backend-agnostic renderer contract: GPU resource lifetime plus command-stream
/// submission, expressed entirely in this package's handle and descriptor types. A backend
/// (Dawn/WebGPU natively, WebGPU through the browser) implements it; renderers built on top —
/// <c>PbrRenderer</c> and its material cache — consume nothing else, so they compose with any
/// backend without a compile-time reference to one.</summary>
/// <remarks>
/// <para>The surface is deliberately exactly what those consumers call, not everything a backend
/// can do. Three families of member stay off it on purpose:</para>
/// <list type="bullet">
/// <item><description><b>Overlay composition</b> (<c>WebGpuRenderer.OverlayPass</c>) is
/// backend-specific for now: it hands the subscriber a native Dawn command encoder and texture
/// view, and its subscribers (the Noesis render device, the ImGui host) are written against those
/// native types. Browser hosts composite UI in the DOM instead, so there is no shared shape to
/// abstract yet. Hosts that record an overlay hold the concrete backend type.</description></item>
/// <item><description><b>Native escape hatches</b> (<c>NativeDevice</c>, <c>ReadbackColor</c>,
/// <c>RenderClearFrame</c>) — backend-specific by definition, or test/screenshot
/// infrastructure.</description></item>
/// <item><description><b>Raw pipeline and shader construction</b> (<c>CreateShader(in
/// ShaderDesc)</c>, <c>CreatePipeline(in PipelineDesc)</c>) — the reflected-program overloads
/// below are the contract; the raw descriptor path is a backend's own plumbing, and pinning it
/// here would force every backend to expose separate shader-module handles.</description></item>
/// </list>
/// <para>Handle lifetime follows the backend's stale-handle contract: a <c>Destroy*</c> call
/// invalidates the handle synchronously, and any later use of it throws rather than silently
/// resolving to a recycled resource.</para>
/// </remarks>
public interface IRenderer
{
    /// <summary>The backend's color-target format — the swapchain format when presenting to a
    /// surface, or the offscreen target's format when headless. Pipeline color targets must match
    /// it or the backend rejects the pipeline.</summary>
    TextureFormat ColorFormat { get; }

    /// <summary>True when the adapter granted BC texture compression — required before creating
    /// textures in any <c>Bc*</c> format; callers without it upload RGBA32-transcoded data.</summary>
    bool SupportsBcTextureCompression { get; }

    /// <summary>Required stride alignment for dynamic uniform-buffer offsets (≥ 256). Uniform
    /// rings must round their per-draw stride up to this.</summary>
    uint UniformBufferOffsetAlignment { get; }

    /// <summary>Resize the presentation target (surface or offscreen texture) to
    /// <paramref name="width"/> x <paramref name="height"/>. Zero-sized requests are clamped
    /// to 1.</summary>
    void Resize(uint width, uint height);

    /// <summary>Create an uninitialized buffer.</summary>
    BufferHandle CreateBuffer(in BufferDesc desc);

    /// <summary>Create a buffer and immediately upload <paramref name="data"/> to it. The buffer
    /// is created with <see cref="BufferUsage.CopyDst"/> implicitly added so the upload can
    /// succeed, and grown to fit <paramref name="data"/> if the descriptor asks for less.</summary>
    BufferHandle CreateBufferWithData<T>(in BufferDesc desc, ReadOnlySpan<T> data) where T : unmanaged;

    /// <summary>Write <paramref name="data"/> into an existing buffer at <paramref name="offset"/>
    /// — the per-frame uniform upload path (frame/draw UBO rings).</summary>
    void UpdateBuffer<T>(BufferHandle handle, ulong offset, ReadOnlySpan<T> data) where T : unmanaged;

    /// <summary>Destroy a buffer. In-flight GPU work referencing it finishes first; the handle
    /// stops resolving immediately.</summary>
    void DestroyBuffer(BufferHandle handle);

    /// <summary>Create a texture. Requesting a <c>Bc*</c> format without
    /// <see cref="SupportsBcTextureCompression"/> throws.</summary>
    TextureHandle CreateTexture(in TextureDesc desc);

    /// <summary>Upload one mip level. <paramref name="bytesPerRow"/> is the source row pitch in
    /// bytes (for BC formats: bytes per row of 4-texel blocks); <paramref name="rowsPerImage"/>
    /// the number of rows (block rows for BC); <paramref name="width"/>/<paramref name="height"/>
    /// the mip's texel dimensions. Block-size math stays in the asset layer.</summary>
    void WriteTexture(TextureHandle handle, uint mipLevel, ReadOnlySpan<byte> data, uint bytesPerRow, uint rowsPerImage, uint width, uint height);

    /// <summary>Destroy a texture. Views created from it must be destroyed separately.</summary>
    void DestroyTexture(TextureHandle handle);

    /// <summary>Create an explicit view into a texture (a chosen dimension / array-layer range) —
    /// e.g. a single layer of the shadow-map array as a render target, or the whole array as a
    /// D2Array sampling view.</summary>
    TextureViewHandle CreateTextureView(in TextureViewDesc desc);

    /// <summary>Destroy a texture view.</summary>
    void DestroyTextureView(TextureViewHandle handle);

    /// <summary>Create a sampler.</summary>
    SamplerHandle CreateSampler(in SamplerDesc desc);

    /// <summary>Destroy a sampler.</summary>
    void DestroySampler(SamplerHandle handle);

    /// <summary>Create a bind group binding concrete resources to one of a program's bind-group
    /// layouts.</summary>
    BindGroupHandle CreateBindGroup(in BindGroupDesc desc);

    /// <summary>Destroy a bind group.</summary>
    void DestroyBindGroup(BindGroupHandle handle);

    /// <summary>Build a render pipeline from a Slang-reflected program plus a target color format.
    /// Vertex layout comes verbatim from the program's reflection record — never hand-coded. The
    /// <paramref name="topology"/> and <paramref name="stripIndexFormat"/> parameters default to
    /// triangle-list / uint16; line / point / strip callers pass their own values rather than
    /// getting silently wrong primitive assembly.</summary>
    /// <param name="fragmentEntryPoint">Selects among multiple <c>[shader("fragment")]</c> entry
    /// points (e.g. linear vs sRGB-encoding); null takes the first.</param>
    /// <param name="vertexEntryPoint">The vertex-side twin, for programs authoring more than one
    /// vertex entry (rigid vs skinned). Selecting the module also selects its reflected vertex
    /// layout: the two must move together, or one entry point's stride is fed to another's
    /// attributes and the draw produces nothing without erroring.</param>
    PipelineHandle CreatePipeline(
        in ShaderProgramDesc program,
        TextureFormat colorFormat,
        PrimitiveTopology topology = PrimitiveTopology.TriangleList,
        IndexFormat stripIndexFormat = IndexFormat.Uint16,
        TextureFormat? depthStencilFormat = null,
        BlendMode blend = BlendMode.Opaque,
        bool depthWriteEnabled = true,
        CompareFunction depthCompare = CompareFunction.Less,
        string? fragmentEntryPoint = null,
        string? vertexEntryPoint = null);

    /// <summary>Build a DEPTH-ONLY pipeline (vertex + depth-stencil, no fragment stage / no color
    /// target) — the shadow-caster path. <paramref name="vertexLayouts"/> overrides the program's
    /// reflected vertex layout so the caster can read position from the full interleaved mesh
    /// buffer (its shadow shader declares only location 0).</summary>
    PipelineHandle CreateDepthOnlyPipeline(
        in ShaderProgramDesc program,
        TextureFormat depthStencilFormat,
        ReadOnlyMemory<VertexBufferLayoutDesc> vertexLayouts,
        CompareFunction depthCompare = CompareFunction.Less,
        string? vertexEntryPoint = null);

    /// <summary>Destroy a pipeline.</summary>
    void DestroyPipeline(PipelineHandle handle);

    /// <summary>Build a COMPUTE pipeline from a Slang-reflected program. Module selection: the
    /// first <see cref="ShaderStage.Compute"/> module, or the one whose entry point matches
    /// <paramref name="entryPoint"/>. The layout comes from <c>program.Layout</c> when it carries
    /// groups (the reflected path), otherwise the backend's implicit layout — exactly like the
    /// render path. Bind and dispatch inside a
    /// <see cref="RenderCommandEncoder.BeginComputePass"/> block.</summary>
    ComputePipelineHandle CreateComputePipeline(in ShaderProgramDesc program, string? entryPoint = null);

    /// <summary>Destroy a compute pipeline.</summary>
    void DestroyComputePipeline(ComputePipelineHandle handle);

    /// <summary>Submit a recorded <see cref="RenderCommandStream"/>: acquire the color target,
    /// execute every <see cref="RenderCommand"/>, present, and advance the frame counter so
    /// deferred destructions can drain. One PRESENTING call per frame — any number of
    /// <see cref="SubmitOffscreen"/> calls may precede it, and queue order guarantees their
    /// results are visible to it.</summary>
    void Submit(in RenderCommandStream stream);

    /// <summary>Submit a stream that touches no backbuffer: every color attachment must carry a
    /// valid <c>ColorView</c> (depth-only and compute passes are fine). Does not acquire or
    /// present the swapchain and does not advance the frame counter — the channel for
    /// game-owned simulation, caustics, and render-to-texture work, submitted any number of
    /// times before the frame's presenting <see cref="Submit"/>.</summary>
    void SubmitOffscreen(in RenderCommandStream stream);
}
