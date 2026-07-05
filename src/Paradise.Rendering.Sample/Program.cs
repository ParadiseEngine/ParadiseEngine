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

    private enum SceneKind
    {
        Triangle, // M1: clear + colored triangle
        Cube,     // M2: textured lit cube with depth (--cube)
        Pbr,      // PR-5: PBR viewer, procedural or GLB (--pbr [path.glb])
    }

    private static int Main(string[] args)
    {
        var headlessFrames = ParseHeadless(args);
        var kind = SceneKind.Triangle;
        string? glbPath = null;
        var pbrIndex = Array.IndexOf(args, "--pbr");
        if (pbrIndex >= 0)
        {
            kind = SceneKind.Pbr;
            if (pbrIndex + 1 < args.Length && !args[pbrIndex + 1].StartsWith("--", StringComparison.Ordinal))
                glbPath = args[pbrIndex + 1];
        }
        else if (Array.IndexOf(args, "--cube") >= 0)
        {
            kind = SceneKind.Cube;
        }

        try
        {
            return headlessFrames is int n ? RunHeadless(n, kind, glbPath) : RunWindowed(kind, glbPath);
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

    private static int RunHeadless(int frameCount, SceneKind kind, string? glbPath)
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
            switch (kind)
            {
                case SceneKind.Pbr:
                {
                    using var scene = new PbrViewerScene(renderer, InitialWidth, InitialHeight, glbPath);
                    for (var i = 0; i < frameCount; i++)
                        scene.RenderFrame();
                    break;
                }
                case SceneKind.Cube:
                {
                    using var scene = new LitCubeScene(renderer, InitialWidth, InitialHeight);
                    for (var i = 0; i < frameCount; i++)
                        scene.RenderFrame();
                    break;
                }
                default:
                {
                    using var scene = new TriangleScene(renderer);
                    for (var i = 0; i < frameCount; i++)
                        scene.RenderFrame();
                    break;
                }
            }
            Console.WriteLine($"Headless mode: rendered {frameCount} {kind} frames against an offscreen target.");
            return 0;
        }
        finally
        {
            SDL_Quit();
        }
    }

    private static unsafe int RunWindowed(SceneKind kind, string? glbPath)
    {
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            Console.Error.WriteLine($"SDL_Init failed: {SDL_GetError()}");
            return 1;
        }

        SDL_Window* window = null;
        IntPtr metalView = IntPtr.Zero;
        WebGpuRenderer? renderer = null;
        try
        {
            window = SDL_CreateWindow("Paradise.Rendering — Clear Color", InitialWidth, InitialHeight, SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
            if (window == null)
            {
                Console.Error.WriteLine($"SDL_CreateWindow failed: {SDL_GetError()}");
                return 1;
            }

            var surfaceDesc = BuildSurfaceDescriptor(window, out metalView);
            renderer = new WebGpuRenderer(in surfaceDesc);
            using var triangleScene = kind == SceneKind.Triangle ? new TriangleScene(renderer) : null;
            using var cubeScene = kind == SceneKind.Cube ? new LitCubeScene(renderer, surfaceDesc.Width, surfaceDesc.Height) : null;
            using var pbrScene = kind == SceneKind.Pbr ? new PbrViewerScene(renderer, surfaceDesc.Width, surfaceDesc.Height, glbPath) : null;

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
                        {
                            renderer.Resize((uint)w, (uint)h);
                            cubeScene?.Resize((uint)w, (uint)h);
                            pbrScene?.Resize((uint)w, (uint)h);
                        }
                    }
                    else if (type == SDL_EventType.SDL_EVENT_MOUSE_MOTION && pbrScene is not null)
                    {
                        // Left-drag orbits the PBR viewer camera.
                        if ((ev.motion.state & SDL_MouseButtonFlags.SDL_BUTTON_LMASK) != 0)
                            pbrScene.Drag(ev.motion.xrel, ev.motion.yrel);
                    }
                    else if (type == SDL_EventType.SDL_EVENT_MOUSE_WHEEL && pbrScene is not null)
                    {
                        pbrScene.Zoom(ev.wheel.y);
                    }
                }
                if (pbrScene is not null) pbrScene.RenderFrame();
                else if (cubeScene is not null) cubeScene.RenderFrame();
                else triangleScene!.RenderFrame();
            }

            return 0;
        }
        finally
        {
            renderer?.Dispose();
            // The Metal view (and its CAMetalLayer) must outlive the renderer's surface.
            if (metalView != IntPtr.Zero) SDL_Metal_DestroyView(metalView);
            if (window != null) SDL_DestroyWindow(window);
            SDL_Quit();
        }
    }

    private static unsafe SurfaceDescriptor BuildSurfaceDescriptor(SDL_Window* window, out IntPtr metalView)
    {
        metalView = IntPtr.Zero;
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
            // SDL owns the CAMetalLayer: SDL_Metal_CreateView attaches a Metal-backed view to
            // the window's content view (must run on the main thread — we are on it; SDL3
            // requires main-thread video on macOS), and SDL_Metal_GetLayer hands back the
            // CAMetalLayer* that Dawn's Cocoa surface source needs. The caller destroys the
            // view only after the renderer (and thus the wgpu surface) is disposed.
            metalView = SDL_Metal_CreateView(window);
            if (metalView == IntPtr.Zero)
                throw new InvalidOperationException($"SDL_Metal_CreateView failed: {SDL_GetError()}");
            var layer = SDL_Metal_GetLayer(metalView);
            if (layer == IntPtr.Zero)
                throw new InvalidOperationException("SDL_Metal_GetLayer returned null — no CAMetalLayer behind the SDL Metal view.");
            return new SurfaceDescriptor(SurfacePlatform.Cocoa, IntPtr.Zero, layer, width, height);
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
