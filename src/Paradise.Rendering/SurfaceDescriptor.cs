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
public readonly struct SurfaceDescriptor : IEquatable<SurfaceDescriptor>
{
    public readonly SurfacePlatform Platform;
    public readonly IntPtr DisplayHandle;
    public readonly IntPtr WindowHandle;
    public readonly uint Width;
    public readonly uint Height;

    public SurfaceDescriptor(
        SurfacePlatform platform,
        IntPtr displayHandle,
        IntPtr windowHandle,
        uint width,
        uint height)
    {
        Platform = platform;
        DisplayHandle = displayHandle;
        WindowHandle = windowHandle;
        Width = width;
        Height = height;
    }

    /// <summary>Headless adapter path — backend skips surface creation entirely.</summary>
    public static SurfaceDescriptor Headless(uint width = 1, uint height = 1) =>
        new(SurfacePlatform.Headless, IntPtr.Zero, IntPtr.Zero, width, height);

    public bool Equals(SurfaceDescriptor other) =>
        Platform == other.Platform
        && DisplayHandle == other.DisplayHandle
        && WindowHandle == other.WindowHandle
        && Width == other.Width
        && Height == other.Height;

    public override bool Equals(object? obj) => obj is SurfaceDescriptor other && Equals(other);
    public override int GetHashCode() => HashCode.Combine((byte)Platform, DisplayHandle, WindowHandle, Width, Height);
    public static bool operator ==(SurfaceDescriptor left, SurfaceDescriptor right) => left.Equals(right);
    public static bool operator !=(SurfaceDescriptor left, SurfaceDescriptor right) => !left.Equals(right);
}
