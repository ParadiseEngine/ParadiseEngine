using System;

namespace Paradise.Rendering;

/// <summary>
/// Encoding-agnostic native surface descriptor. The consumer (sample / engine glue) populates
/// the platform tag and raw native handles read from its windowing library; the rendering
/// backend OS-dispatches on <see cref="Platform"/> to wrap the right native surface variant.
/// <para>
/// <see cref="DisplayHandle"/> meaning is platform-specific:
/// Wayland — <c>wl_display*</c>; Xlib — <c>Display*</c>; otherwise unused.
/// <see cref="WindowHandle"/> meaning is platform-specific:
/// Win32 — <c>HWND</c>; Wayland — <c>wl_surface*</c>; Xlib — <c>Window</c> XID;
/// Cocoa — <c>NSWindow*</c> or <c>CAMetalLayer*</c>.
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
