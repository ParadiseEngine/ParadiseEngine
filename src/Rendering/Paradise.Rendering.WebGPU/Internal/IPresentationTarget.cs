using System;
using WgTextureViewHandle = WebGpuSharp.FFI.TextureViewHandle;
using WgTexture = WebGpuSharp.Texture;
using WgTextureView = WebGpuSharp.TextureView;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Owns frame-target acquisition, presentation and readback lifetime.</summary>
/// <remarks>SurfaceTarget borrows a swapchain texture until Present; OffscreenTarget owns a
/// persistent texture. Readable is available only for persistent targets. CurrentTexture supports
/// capture between acquisition and presentation when the surface was configured for
/// copying.</remarks>
internal interface IPresentationTarget : IDisposable
{
    /// <summary>The colour format a pipeline must target, or the backend rejects it at draw time.</summary>
    TextureFormat ColorFormat { get; }

    uint Width { get; }

    uint Height { get; }

    /// <summary>Resize the target. Zero is clamped to 1; an unchanged size is a no-op.</summary>
    void Resize(uint width, uint height);

    /// <summary>The view to render into this frame, or false to SKIP the frame — a swapchain can
    /// report itself outdated (a resize landed, the display changed) and rebuild instead of
    /// yielding a texture, and drawing into a stale one is invalid.</summary>
    /// <remarks>A raw handle the target keeps ownership of until <see cref="Present"/>, so a frame
    /// creates no managed wrapper, and nothing waits on a finalizer to release it.</remarks>
    bool TryAcquireView(out WgTextureViewHandle view);

    /// <summary>A managed wrapper of the acquired view, for native host passes, which take one.</summary>
    /// <remarks>A surface target makes it on first use in a frame; frames without a host pass on the
    /// backbuffer allocate none.</remarks>
    WgTextureView CurrentView { get; }

    /// <summary>Hand the frame back to whoever displays it. A target with no display does
    /// nothing — which is a no-op rather than an error, because "rendered but shown to nobody" is
    /// exactly what a headless run is.</summary>
    void Present();

    /// <summary>Whether acquisition or presentation blocks once enough frames are queued. A
    /// swapchain does; a target with no display does not, so the renderer paces it instead.</summary>
    bool PacesFrames { get; }

    /// <summary>The texture a caller may copy out of AFTER rendering, or null when this target has
    /// none that outlives a frame.</summary>
    WgTexture? Readable { get; }

    /// <summary>Exposes the current texture between successful acquisition and
    /// presentation.</summary>
    /// <remarks>A surface target returns null before acquisition and after presentation; an
    /// offscreen target exposes its persistent texture throughout its lifetime.</remarks>
    WgTexture? CurrentTexture { get; }

    /// <summary>Whether a mid-frame copy out of <see cref="CurrentTexture"/> is permitted. False
    /// when the target's textures were not created <c>CopySrc</c> — for a swapchain that depends on
    /// what the surface was configured to allow, and on whether the platform allows it at
    /// all.</summary>
    bool SupportsCapture { get; }
}
