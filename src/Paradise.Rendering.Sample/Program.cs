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
        var headlessFrames = ParseFlag(args, "--headless");
        var soakFrames = ParseFlag(args, "--soak");
        try
        {
            if (headlessFrames is int h) return RunHeadless(h);
            if (soakFrames is int s) return RunSoak(s);
            return RunWindowed();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Sample failed: {ex}");
            return 1;
        }
    }

    private static int? ParseFlag(string[] args, string flag)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != flag) continue;
            if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var n) || n <= 0)
            {
                Console.Error.WriteLine($"Usage: {flag} <positive integer>");
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
            using var scene = new TriangleScene(renderer);
            for (var i = 0; i < frameCount; i++)
                scene.RenderFrame();
            Console.WriteLine($"Headless mode: rendered {frameCount} triangle frames against an offscreen target.");
            return 0;
        }
        finally
        {
            SDL_Quit();
        }
    }

    // --soak N: create a real SDL3 window, render N frames, report per-frame timing stats.
    // Exercises the full SDL3 → SurfaceDescriptor → WebGpuRenderer → TriangleScene path so
    // surface mapping regressions surface in a live windowed session, not just in unit tests.
    private static unsafe int RunSoak(int frameCount)
    {
        if (frameCount < 0) return 1;

        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            Console.Error.WriteLine($"SDL_Init failed: {SDL_GetError()}");
            return 1;
        }

        SDL_Window* window = null;
        WebGpuRenderer? renderer = null;
        try
        {
            window = SDL_CreateWindow("Paradise.Rendering — Soak", InitialWidth, InitialHeight, SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
            if (window == null)
            {
                Console.Error.WriteLine($"SDL_CreateWindow failed: {SDL_GetError()}");
                return 1;
            }

            var surfaceDesc = BuildSurfaceDescriptor(window);
            renderer = new WebGpuRenderer(in surfaceDesc);
            using var scene = new TriangleScene(renderer);

            var frameTimes = new long[frameCount];
            var sw = System.Diagnostics.Stopwatch.StartNew();

            for (var i = 0; i < frameCount; i++)
            {
                // Drain pending window events so the OS doesn't mark the window as unresponsive.
                SDL_Event ev;
                while (SDL_PollEvent(&ev))
                {
                    var type = (SDL_EventType)ev.type;
                    if (type == SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED ||
                        type == SDL_EventType.SDL_EVENT_WINDOW_RESIZED)
                    {
                        var w = ev.window.data1;
                        var h = ev.window.data2;
                        if (w > 0 && h > 0)
                            renderer.Resize((uint)w, (uint)h);
                    }
                }

                var t0 = sw.ElapsedTicks;
                scene.RenderFrame();
                frameTimes[i] = sw.ElapsedTicks - t0;
            }

            // Report timing stats.
            var ticksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;
            Array.Sort(frameTimes);
            var minMs = frameTimes[0] / ticksPerMs;
            var maxMs = frameTimes[frameCount - 1] / ticksPerMs;
            var avgMs = Array.ConvertAll(frameTimes, t => (double)t).Average() / ticksPerMs;
            var p50Ms = frameTimes[frameCount / 2] / ticksPerMs;
            var p99Ms = frameTimes[(int)(frameCount * 0.99)] / ticksPerMs;

            Console.WriteLine($"Soak: {frameCount} frames  min={minMs:F2}ms  p50={p50Ms:F2}ms  p99={p99Ms:F2}ms  avg={avgMs:F2}ms  max={maxMs:F2}ms");
            return 0;
        }
        finally
        {
            renderer?.Dispose();
            if (window != null) SDL_DestroyWindow(window);
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
            using var scene = new TriangleScene(renderer);

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
                scene.RenderFrame();
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
