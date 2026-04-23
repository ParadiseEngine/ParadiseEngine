using System;
using WebGpuSharp.FFI;
using WgInstance = WebGpuSharp.Instance;
using WgSurface = WebGpuSharp.Surface;
using WgSurfaceDescriptor = WebGpuSharp.SurfaceDescriptor;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>OS-dispatched native surface creation. Maps a <see cref="SurfaceDescriptor"/> from
/// <c>Paradise.Rendering</c> onto the appropriate WebGPUSharp <c>SurfaceSource*FFI</c> chained
/// struct. The headless path is rejected here: callers must skip surface creation entirely and
/// take the headless adapter path in <see cref="WebGpuDevice"/>.</summary>
internal static unsafe class SurfaceFactory
{
    public static WgSurface Create(WgInstance instance, in SurfaceDescriptor desc)
    {
        return desc.Platform switch
        {
            SurfacePlatform.Win32 => CreateWin32(instance, desc.WindowHandle),
            SurfacePlatform.Xlib => CreateXlib(instance, desc.DisplayHandle, desc.WindowHandle),
            SurfacePlatform.Wayland => CreateWayland(instance, desc.DisplayHandle, desc.WindowHandle),
            SurfacePlatform.Cocoa => CreateMetalLayer(instance, desc.WindowHandle),
            SurfacePlatform.Headless => throw new InvalidOperationException(
                "Headless surfaces must skip CreateSurface entirely and use the headless adapter path."),
            _ => throw new NotSupportedException($"Surface platform '{desc.Platform}' is not supported by the WebGPU backend."),
        };
    }

    private static WgSurface CreateWin32(WgInstance instance, IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) throw new ArgumentException("Win32 surface requires a non-null HWND.", nameof(hwnd));
        var src = new SurfaceSourceWindowsHWNDFFI
        {
            // Hinstance is optional in Dawn — leaving it null lets the implementation pick.
            Hinstance = null,
            Hwnd = (void*)hwnd,
        };
        return CreateOrThrow(instance, new WgSurfaceDescriptor(ref src), nameof(SurfacePlatform.Win32));
    }

    private static WgSurface CreateXlib(WgInstance instance, IntPtr display, IntPtr window)
    {
        if (display == IntPtr.Zero) throw new ArgumentException("Xlib surface requires a non-null Display*.", nameof(display));
        if (window == IntPtr.Zero) throw new ArgumentException("Xlib surface requires a non-null Window XID.", nameof(window));
        var src = new SurfaceSourceXlibWindowFFI
        {
            Display = (void*)display,
            Window = (ulong)window.ToInt64(),
        };
        return CreateOrThrow(instance, new WgSurfaceDescriptor(ref src), nameof(SurfacePlatform.Xlib));
    }

    private static WgSurface CreateWayland(WgInstance instance, IntPtr display, IntPtr surface)
    {
        if (display == IntPtr.Zero) throw new ArgumentException("Wayland surface requires a non-null wl_display*.", nameof(display));
        if (surface == IntPtr.Zero) throw new ArgumentException("Wayland surface requires a non-null wl_surface*.", nameof(surface));
        var src = new SurfaceSourceWaylandSurfaceFFI
        {
            Display = (void*)display,
            Surface = (void*)surface,
        };
        return CreateOrThrow(instance, new WgSurfaceDescriptor(ref src), nameof(SurfacePlatform.Wayland));
    }

    private static WgSurface CreateMetalLayer(WgInstance instance, IntPtr metalLayer)
    {
        if (metalLayer == IntPtr.Zero) throw new ArgumentException("Cocoa surface requires a non-null CAMetalLayer*.", nameof(metalLayer));
        var src = new SurfaceSourceMetalLayerFFI
        {
            Layer = (void*)metalLayer,
        };
        return CreateOrThrow(instance, new WgSurfaceDescriptor(ref src), nameof(SurfacePlatform.Cocoa));
    }

    private static WgSurface CreateOrThrow(WgInstance instance, WgSurfaceDescriptor descriptor, string platformLabel)
    {
        return instance.CreateSurface(descriptor)
            ?? throw new InvalidOperationException($"Instance.CreateSurface returned null for platform '{platformLabel}'.");
    }
}
