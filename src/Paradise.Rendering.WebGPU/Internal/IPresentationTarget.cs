using System;
using WgTexture = WebGpuSharp.Texture;
using WgTextureView = WebGpuSharp.TextureView;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>
/// WHERE a frame is drawn, and what happens to it afterwards.
///
/// The renderer used to answer this with a <c>bool _isHeadless</c> consulted at six points — the
/// colour format, the resize, the two presents, the readback guard, and the backbuffer acquire.
/// That put the property on the wrong object: "headless" is not a kind of RENDERER, it is a kind
/// of TARGET. A renderer paints; whether the canvas is lent by a window or owned outright is the
/// canvas's business.
///
/// So the two differ here, once, and the renderer's frame path has no idea which it has:
///
/// <list type="bullet">
/// <item><see cref="SurfaceTarget"/> BORROWS a texture from the platform's swapchain each frame
/// and hands it back with <see cref="Present"/>.</item>
/// <item><see cref="OffscreenTarget"/> OWNS one texture, lends the same one every frame, and has
/// nothing to present to.</item>
/// </list>
///
/// <see cref="Readable"/> is where that difference stops being an implementation detail: a
/// swapchain texture belongs to the presentation engine and is not created <c>CopySrc</c>, so it
/// cannot be copied out of. An owned one is. That is the whole reason a played session cannot be
/// screenshotted from inside the engine while a headless run can.
/// </summary>
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
    bool TryAcquireView(out WgTextureView view);

    /// <summary>Hand the frame back to whoever displays it. A target with no display does
    /// nothing — which is a no-op rather than an error, because "rendered but shown to nobody" is
    /// exactly what a headless run is.</summary>
    void Present();

    /// <summary>The texture a caller may copy out of, or null when this target's texture belongs
    /// to the presentation engine and cannot be read.</summary>
    WgTexture? Readable { get; }
}
