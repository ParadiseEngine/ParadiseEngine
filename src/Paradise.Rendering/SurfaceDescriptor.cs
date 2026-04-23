using System;

namespace Paradise.Rendering;

/// <summary>
/// Encoding-agnostic native surface descriptor. The consumer (sample / engine glue) populates
/// the platform tag and raw native handles read from its windowing library; the rendering
/// backend OS-dispatches on <see cref="Platform"/> to wrap the right native surface variant.
/// <para>
/// <see cref="DisplayHandle"/> meaning is platform-specific:
/// Wayland — <c>wl_display*</c>; Xlib — <c>Display*</c>; otherwise unused.
/// </para>
/// <para>
/// <see cref="WindowHandle"/> meaning is platform-specific:
/// Win32 — <c>HWND</c>; Wayland — <c>wl_surface*</c>; Xlib — <c>Window</c> XID;
/// Cocoa — <c>CAMetalLayer*</c> (the consumer is responsible for creating the layer on the
/// main thread and attaching it to <c>NSWindow.contentView.layer</c> before populating this
/// descriptor; passing a raw <c>NSWindow*</c> is unsupported because it leaves the layer-creation
/// thread/lifetime contract ambiguous to the backend).
/// </para>
/// </summary>
public readonly record struct SurfaceDescriptor(
    SurfacePlatform Platform,
    IntPtr DisplayHandle,
    IntPtr WindowHandle,
    uint Width,
    uint Height)
{
    /// <summary>Headless adapter path — backend skips surface creation entirely.</summary>
    public static SurfaceDescriptor Headless(uint width = 1, uint height = 1) =>
        new(SurfacePlatform.Headless, IntPtr.Zero, IntPtr.Zero, width, height);
}
