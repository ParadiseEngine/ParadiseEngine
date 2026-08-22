using System;

namespace Paradise.Windowing;

/// <summary>
/// Encoding-agnostic native surface descriptor: the handles a renderer needs in order to
/// present into a window. A window PRODUCES one (<see cref="IWindow.CreateSurface"/>) and a
/// rendering backend CONSUMES it, OS-dispatching on <see cref="Platform"/> to wrap the right
/// native surface variant.
///
/// It lives with the windowing contract rather than the rendering one because that is what it
/// describes — an HWND, a CAMetalLayer, a wl_surface. Having it the other way round made
/// Paradise.Windowing depend on all of Paradise.Rendering for this one struct, which in turn
/// put a renderer in the dependency closure of anything that merely wanted the key vocabulary.
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
