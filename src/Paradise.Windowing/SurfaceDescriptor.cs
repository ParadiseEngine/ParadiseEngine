using System;

namespace Paradise.Windowing;

/// <summary>Carries the native handles needed to present into a window.</summary>
/// <remarks>DisplayHandle is wl_display* on Wayland, Display* on Xlib, otherwise unused.
/// WindowHandle is HWND, wl_surface*, an Xlib Window ID, CAMetalLayer* or ANativeWindow*.
/// For Cocoa, create and attach the layer on the main thread; raw NSWindow pointers are unsupported.
/// Android descriptors borrow the window owner's ANativeWindow: stop rendering before its drawable
/// is destroyed and obtain a new descriptor after replacement. This value does not acquire a native
/// reference or transfer ownership. This contract lives in Windowing to avoid a rendering dependency.</remarks>
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
