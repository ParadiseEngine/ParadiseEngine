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
/// <see cref="Readable"/> is where that difference stops being an implementation detail — and the
/// reason is LIFETIME, not permission. An owned texture is the same object every frame and stays
/// valid until the target is disposed, so it can be read whenever. A borrowed one is valid only
/// between its acquire and its present: afterwards it belongs to the compositor and is rotated
/// back into the chain, so by the time a caller has finished rendering there is nothing left to
/// read.
///
/// (The swapchain's textures are also configured <c>RenderAttachment</c> only — see
/// <see cref="SurfaceState"/> — so they are not copyable today either. That part IS ours to
/// change: <c>GetCapabilities</c> reports which usages the surface supports and we currently read
/// only its formats. Adding <c>CopySrc</c> would make a WINDOWED capture possible, but only as a
/// mid-frame one, recorded before the present that ends the texture's life. It would not make
/// <see cref="Readable"/> meaningful here.)
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

    /// <summary>The texture a caller may copy out of AFTER rendering, or null when this target has
    /// none that outlives a frame.</summary>
    WgTexture? Readable { get; }
}
