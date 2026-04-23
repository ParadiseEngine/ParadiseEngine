using System;
using System.Runtime.InteropServices;
using Paradise.Rendering;
using Paradise.Rendering.WebGPU;
using static SDL.SDL3;
using SDL;

namespace Paradise.Rendering.Sample;

internal static class Program
{
    private const int InitialWidth = 640;
    private const int InitialHeight = 480;

    private static int Main(string[] args)
    {
        var headlessFrames = ParseHeadless(args);
        try
        {
            return headlessFrames is int n ? RunHeadless(n) : RunWindowed();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Sample failed: {ex}");
            return 1;
        }
    }

    private static int? ParseHeadless(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != "--headless") continue;
            if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var n) || n <= 0)
            {
                Console.Error.WriteLine("Usage: --headless <positive integer>");
                return -1;
            }
            return n;
        }
        return null;
    }

    private static int RunHeadless(int frameCount)
    {
        if (frameCount < 0) return 1;

        // SDL still needs to initialize cleanly even though no window is opened — the dummy
        // video driver lets it succeed in headless CI containers without a display server.
        // Using SDL_SetHint instead of Environment.SetEnvironmentVariable: native libc getenv()
        // can cache values from process start, so the managed env var doesn't reliably reach
        // SDL's video subsystem; SDL_SetHint writes through the SDL hint API directly.
        SDL_SetHint(SDL_HINT_VIDEO_DRIVER, "dummy"u8);

        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            Console.Error.WriteLine($"SDL_Init failed: {SDL_GetError()}");
            return 1;
        }

        try
        {
            using var renderer = WebGpuRenderer.CreateHeadless(InitialWidth, InitialHeight);
            for (var i = 0; i < frameCount; i++)
                renderer.RenderClearFrame(ColorRgba.CornflowerBlue);
            Console.WriteLine($"Headless mode: rendered {frameCount} clear frames against an offscreen target.");
            return 0;
        }
        finally
        {
            SDL_Quit();
        }
    }

    private static unsafe int RunWindowed()
    {
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            Console.Error.WriteLine($"SDL_Init failed: {SDL_GetError()}");
            return 1;
        }

        SDL_Window* window = null;
        WebGpuRenderer? renderer = null;
        try
        {
            window = SDL_CreateWindow("Paradise.Rendering — Clear Color", InitialWidth, InitialHeight, SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
            if (window == null)
            {
                Console.Error.WriteLine($"SDL_CreateWindow failed: {SDL_GetError()}");
                return 1;
            }

            var surfaceDesc = BuildSurfaceDescriptor(window);
            renderer = new WebGpuRenderer(in surfaceDesc);

            var quit = false;
            SDL_Event ev;
            while (!quit)
            {
                while (SDL_PollEvent(&ev))
                {
                    var type = (SDL_EventType)ev.type;
                    if (type == SDL_EventType.SDL_EVENT_QUIT)
                    {
                        quit = true;
                    }
                    else if (type == SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED)
                    {
                        quit = true;
                    }
                    else if (type == SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED ||
                             type == SDL_EventType.SDL_EVENT_WINDOW_RESIZED)
                    {
                        var w = ev.window.data1;
                        var h = ev.window.data2;
                        if (w > 0 && h > 0)
                            renderer.Resize((uint)w, (uint)h);
                    }
                }
                renderer.RenderClearFrame(ColorRgba.CornflowerBlue);
            }

            return 0;
        }
        finally
        {
            renderer?.Dispose();
            if (window != null) SDL_DestroyWindow(window);
            SDL_Quit();
        }
    }

    private static unsafe SurfaceDescriptor BuildSurfaceDescriptor(SDL_Window* window)
    {
        var props = SDL_GetWindowProperties(window);

        int w = 0, h = 0;
        SDL_GetWindowSizeInPixels(window, &w, &h);
        var width = (uint)Math.Max(1, w);
        var height = (uint)Math.Max(1, h);

        if (OperatingSystem.IsWindows())
        {
            var hwnd = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_WIN32_HWND_POINTER, IntPtr.Zero);
            return new SurfaceDescriptor(SurfacePlatform.Win32, IntPtr.Zero, hwnd, width, height);
        }

        if (OperatingSystem.IsMacOS())
        {
            // The Cocoa surface path expects a CAMetalLayer*, but SDL3 only exposes the
            // NSWindow*. Wiring CAMetalLayer (creation on the main thread + attachment to the
            // NSWindow content view) is out of scope for M0b per the issue spec. Refuse the
            // windowed path explicitly rather than feeding the wrong pointer to Dawn — the
            // headless adapter path (--headless N) covers macOS for now.
            throw new PlatformNotSupportedException(
                "Windowed macOS is not yet wired: SDL3 exposes NSWindow* but the Cocoa surface " +
                "path requires a CAMetalLayer*. CAMetalLayer creation and attachment will land " +
                "with full Cocoa support; use --headless N on macOS for now.");
        }

        if (OperatingSystem.IsLinux())
        {
            var wlDisplay = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_WAYLAND_DISPLAY_POINTER, IntPtr.Zero);
            if (wlDisplay != IntPtr.Zero)
            {
                var wlSurface = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_WAYLAND_SURFACE_POINTER, IntPtr.Zero);
                return new SurfaceDescriptor(SurfacePlatform.Wayland, wlDisplay, wlSurface, width, height);
            }

            var x11Display = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_X11_DISPLAY_POINTER, IntPtr.Zero);
            var x11Window = SDL_GetNumberProperty(props, SDL_PROP_WINDOW_X11_WINDOW_NUMBER, 0);
            return new SurfaceDescriptor(SurfacePlatform.Xlib, x11Display, (IntPtr)x11Window, width, height);
        }

        throw new PlatformNotSupportedException(
            $"Surface mapping for the current OS ({RuntimeInformation.OSDescription}) is not implemented; " +
            "use --headless on this platform.");
    }
}
