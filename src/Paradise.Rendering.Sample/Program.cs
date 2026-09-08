using Microsoft.Extensions.Logging;

using Paradise.Diagnostics;

using Paradise.Windowing;
using System;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering;
using Paradise.Rendering.Pbr;
using Paradise.Rendering.WebGPU;
using static SDL.SDL3;
using SDL;

namespace Paradise.Rendering.Sample;

internal static class Program
{
    // --size WxH overrides the default window / headless target size.
    private static uint InitialWidth = 640;
    private static uint InitialHeight = 480;

    /// <summary>Provides the sample's shared diagnostic sink.</summary>
    /// <remarks>Configure once from --log-level and create loggers by category.</remarks>
    private static ILoggerFactory s_log = ParadiseConsole.CreateFactory(new ParadiseConsoleOptions());

    private enum SceneKind
    {
        Triangle, // M1: clear + colored triangle
        Cube,     // M2: textured lit cube with depth (--cube)
        Pbr,      // PR-5: PBR viewer, procedural or GLB (--pbr [path.glb])
        Compute,  // v0.9: compute-written plasma via SubmitOffscreen (--compute)
        GiDemo,   // the probe GI test room, static + moving lights (--gi-demo [model.glb])
        SsrDemo,  // the screen-space reflection floor (--ssr-demo)
    }

    private static int s_screenshotEvery;
    private static bool s_bench;

    /// <summary>The engine's feature configuration for this run, handed to every
    /// <see cref="PbrRenderer"/> the sample builds. One object per process: it is the engine's, not
    /// the renderer's, and a host that grew an ECS schedule or a debug UI would pass this same one
    /// to those too.</summary>
    internal static FeatureSwitches Features { get; private set; } = new();

    private static int Main(string[] args)
    {
        if (ParseLogLevel(args) is not { } level) return 1;
        s_log = ParadiseConsole.CreateFactory(new ParadiseConsoleOptions { MinLevel = level });

        if (ParseFeatures(args) is not { } features) return 1;
        Features = features;
        if (Array.IndexOf(args, "--list-features") >= 0)
        {
            ListFeatures();
            return 0;
        }

        if (Array.IndexOf(args, "--showcase") >= 0)
        {
            try { return RendererShowcase.Run(args, s_log); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Showcase failed: {ex}");
                return 1;
            }
        }
        var headlessFrames = ParseHeadless(args);
        var screenshotPath = ParseValue(args, "--screenshot");
        s_bench = Array.IndexOf(args, "--bench") >= 0;
#if !PARADISE_PROFILING
        if (s_bench)
        {
            Console.Error.WriteLine("--bench needs a profiling build: dotnet build -p:ParadiseProfiling=true");
            return 1;
        }
#endif
        if (ParseValue(args, "--size") is { } size && size.Split('x') is [var sw, var sh]
            && uint.TryParse(sw, out var width) && uint.TryParse(sh, out var height) && width > 0 && height > 0)
        {
            InitialWidth = width;
            InitialHeight = height;
        }
        // --screenshot-every N writes <screenshot>-NNNN.png every N frames: how a flicker is found.
        s_screenshotEvery = int.TryParse(ParseValue(args, "--screenshot-every"), out var every) && every > 0 ? every : 0;
        // Global-illumination switches for the PBR viewer; the scene reads them when it is built.
        PbrViewerScene.RayTracedAo = Array.IndexOf(args, "--rtao") >= 0;
        PbrViewerScene.ProbeGi = Array.IndexOf(args, "--gi") >= 0;
        PbrViewerScene.Reflections = Array.IndexOf(args, "--ssr") >= 0;
        var kind = SceneKind.Triangle;
        string? glbPath = null;
        var pbrIndex = Array.IndexOf(args, "--pbr");
        var giDemoIndex = Array.IndexOf(args, "--gi-demo");
        if (pbrIndex >= 0)
        {
            kind = SceneKind.Pbr;
            if (pbrIndex + 1 < args.Length && !args[pbrIndex + 1].StartsWith("--", StringComparison.Ordinal))
                glbPath = args[pbrIndex + 1];
        }
        else if (giDemoIndex >= 0)
        {
            kind = SceneKind.GiDemo;
            if (giDemoIndex + 1 < args.Length && !args[giDemoIndex + 1].StartsWith("--", StringComparison.Ordinal))
                glbPath = args[giDemoIndex + 1];
            GiDemoScene.ProbeGi = Array.IndexOf(args, "--no-gi") < 0;
            GiDemoScene.RayTracedAo = PbrViewerScene.RayTracedAo;
            GiDemoScene.Reflections = PbrViewerScene.Reflections;
            GiDemoScene.Decals = Array.IndexOf(args, "--decals") >= 0;
            GiDemoScene.Fog = Array.IndexOf(args, "--fog") >= 0;
            GiDemoScene.AnimateLights = Array.IndexOf(args, "--static-lights") < 0;
            GiDemoScene.PanelOnly = Array.IndexOf(args, "--panel-only") >= 0;
            if (int.TryParse(ParseValue(args, "--gi-rays"), out var giRays)) GiDemoScene.RaysPerProbe = giRays;
            if (int.TryParse(ParseValue(args, "--gi-max-probes"), out var giMax)) GiDemoScene.MaxProbes = giMax;
            if (int.TryParse(ParseValue(args, "--gi-probes-per-frame"), out var giWindow)) GiDemoScene.ProbesPerFrame = giWindow;
            if (int.TryParse(ParseValue(args, "--lights"), out var extraLights)) GiDemoScene.ExtraLights = extraLights;
        }
        else if (Array.IndexOf(args, "--ssr-demo") >= 0)
        {
            kind = SceneKind.SsrDemo;
            SsrDemoScene.Reflections = Array.IndexOf(args, "--no-ssr") < 0;
            SsrDemoScene.Animate = Array.IndexOf(args, "--static-lights") < 0;
        }
        else if (Array.IndexOf(args, "--cube") >= 0)
        {
            kind = SceneKind.Cube;
        }
        else if (Array.IndexOf(args, "--compute") >= 0)
        {
            kind = SceneKind.Compute;
        }

        try
        {
            return headlessFrames is int n ? RunHeadless(n, kind, glbPath, screenshotPath) : RunWindowed(kind, glbPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Sample failed: {ex}");
            return 1;
        }
    }

    /// <summary>Builds feature configuration from file, environment and command line in that
    /// precedence order.</summary>
    /// <remarks>Returns null after reporting invalid arguments. Declare built-ins before acquiring
    /// a GPU so --list-features and unknown-name checks work without an adapter.</remarks>
    private static FeatureSwitches? ParseFeatures(string[] args)
    {
        var configuration = EngineConfiguration.Empty;
        try
        {
            if (ParseValue(args, "--config") is { } path)
            {
                using var file = File.OpenRead(path);
                configuration = TomlEngineConfiguration.Read(file);
            }
            configuration = configuration
                .Merge(new EngineConfiguration { Features = FeatureOverrides.FromEnvironment() })
                .Merge(new EngineConfiguration { Features = FeatureOverrides.Parse(ParseValue(args, "--features")) });
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Feature configuration: {error.Message}");
            return null;
        }

        // The whole document, not just its switches: a game feature's settings travel with it.
        var switches = new FeatureSwitches(configuration);
        PbrFeatures.DeclareAll(switches);
        // A name nothing declares is a stale line in somebody's config, not a feature that is
        // off — worth saying out loud, and not worth refusing to start over.
        foreach (var name in switches.Unknown)
            Console.Error.WriteLine($"Feature configuration: no feature named '{name}' in this build.");
        return switches;
    }

    /// <summary>What this build can switch, and where each stands right now.</summary>
    private static void ListFeatures()
    {
        foreach (var definition in Features.Definitions.OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            var state = Features.IsEnabled(definition.Id) ? "on " : "off";
            Console.WriteLine($"  {state}  {definition.Name,-36}  {definition.Summary}");
        }
    }

    /// <summary>The value after <paramref name="flag"/>, or null when the flag is absent.</summary>
    private static string? ParseValue(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary><c>--log-level &lt;level&gt;</c>, or Information. Null means the argument was bad and was reported.</summary>
    /// <remarks><c>Debug</c> is what turns the PBR cluster dump on — the froxel counts that
    /// <c>PARADISE_CLUSTER_DEBUG=1</c> used to switch.</remarks>
    private static LogLevel? ParseLogLevel(string[] args)
    {
        var index = Array.IndexOf(args, "--log-level");
        if (index < 0) return LogLevel.Information;

        if (index + 1 >= args.Length || !Enum.TryParse<LogLevel>(args[index + 1], ignoreCase: true, out var level))
        {
            Console.Error.WriteLine(
                $"Usage: --log-level <{string.Join('|', Enum.GetNames<LogLevel>())}>  (Debug shows the PBR cluster dump)");
            return null;
        }

        return level;
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

    private static int RunHeadless(int frameCount, SceneKind kind, string? glbPath, string? screenshotPath)
    {
        if (frameCount < 0) return 1;

        // Set SDL's dummy video hint directly for headless CI; managed environment changes may not
        // reach native getenv caches.
        SDL_SetHint(SDL_HINT_VIDEO_DRIVER, "dummy"u8);

        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            Console.Error.WriteLine($"SDL_Init failed: {SDL_GetError()}");
            return 1;
        }

        try
        {
            using var renderer = WebGpuRenderer.CreateHeadless(InitialWidth, InitialHeight, s_log.CreateLogger("WebGPU"));
            Action<int>? afterFrame = null;
            if (screenshotPath is not null && s_screenshotEvery > 0)
            {
                var stem = Path.ChangeExtension(screenshotPath, null);
                afterFrame = frame =>
                {
                    if (frame % s_screenshotEvery != 0) return;
                    var pixels = renderer.ReadbackColor(out var width, out var height);
                    using var file = File.Create($"{stem}-{frame:D4}.png");
                    PngWriter.Write(file, new ColorReadback(pixels, width, height), renderer.ColorFormat);
                };
            }
            switch (kind)
            {
                case SceneKind.Pbr:
                {
                    using var scene = new PbrViewerScene(renderer, InitialWidth, InitialHeight, glbPath, s_log.CreateLogger("PbrRenderer"));
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
                case SceneKind.Compute:
                {
                    using var scene = new ComputeScene(renderer);
                    for (var i = 0; i < frameCount; i++)
                        scene.RenderFrame();
                    break;
                }
                case SceneKind.GiDemo:
                {
                    using var scene = new GiDemoScene(renderer, InitialWidth, InitialHeight, glbPath, s_log.CreateLogger("PbrRenderer"));
#if PARADISE_PROFILING
                    var bench = s_bench ? new PassBenchmark(renderer) : null;
#endif
                    for (var i = 0; i < frameCount; i++)
                    {
#if PARADISE_PROFILING
                        bench?.BeginFrame();
#endif
                        scene.RenderFrame();
#if PARADISE_PROFILING
                        bench?.Record(i, scene.Renderer);
#endif
                        afterFrame?.Invoke(i);
                    }
#if PARADISE_PROFILING
                    bench?.Report(frameCount);
                    if (bench is not null && scene.Renderer.Pipeline.Find<ProbeGiFeature>()?.ActiveVolume is { } volume)
                        Console.WriteLine($"[bench] probe volume {volume.CountX}x{volume.CountY}x{volume.CountZ} = {volume.CountX * volume.CountY * volume.CountZ} probes, spacing {volume.Spacing.X:F2} m");
#endif
                    break;
                }
                case SceneKind.SsrDemo:
                {
                    using var scene = new SsrDemoScene(renderer, InitialWidth, InitialHeight, s_log.CreateLogger("PbrRenderer"));
                    for (var i = 0; i < frameCount; i++)
                    {
                        scene.RenderFrame();
                        afterFrame?.Invoke(i);
                    }
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
            if (screenshotPath is not null)
            {
                var pixels = renderer.ReadbackColor(out var width, out var height);
                using var file = File.Create(screenshotPath);
                PngWriter.Write(file, new ColorReadback(pixels, width, height), renderer.ColorFormat);
                Console.WriteLine($"Screenshot written to {screenshotPath}.");
            }
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
            window = SDL_CreateWindow("Paradise.Rendering — Clear Color", (int)InitialWidth, (int)InitialHeight, SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
            if (window == null)
            {
                Console.Error.WriteLine($"SDL_CreateWindow failed: {SDL_GetError()}");
                return 1;
            }

            var surfaceDesc = BuildSurfaceDescriptor(window, out metalView);
            renderer = new WebGpuRenderer(in surfaceDesc, logger: s_log.CreateLogger("WebGPU"));
            using var triangleScene = kind == SceneKind.Triangle ? new TriangleScene(renderer) : null;
            using var cubeScene = kind == SceneKind.Cube ? new LitCubeScene(renderer, surfaceDesc.Width, surfaceDesc.Height) : null;
            using var computeScene = kind == SceneKind.Compute ? new ComputeScene(renderer) : null;
            using var pbrScene = kind == SceneKind.Pbr ? new PbrViewerScene(renderer, surfaceDesc.Width, surfaceDesc.Height, glbPath, s_log.CreateLogger("PbrRenderer")) : null;
            using var giScene = kind == SceneKind.GiDemo ? new GiDemoScene(renderer, surfaceDesc.Width, surfaceDesc.Height, glbPath, s_log.CreateLogger("PbrRenderer")) : null;
            using var ssrScene = kind == SceneKind.SsrDemo ? new SsrDemoScene(renderer, surfaceDesc.Width, surfaceDesc.Height, s_log.CreateLogger("PbrRenderer")) : null;

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
                            giScene?.Resize((uint)w, (uint)h);
                            ssrScene?.Resize((uint)w, (uint)h);
                        }
                    }
                    else if (type == SDL_EventType.SDL_EVENT_MOUSE_MOTION && giScene is not null)
                    {
                        if ((ev.motion.state & SDL_MouseButtonFlags.SDL_BUTTON_LMASK) != 0)
                            giScene.Drag(ev.motion.xrel, ev.motion.yrel);
                    }
                    else if (type == SDL_EventType.SDL_EVENT_MOUSE_WHEEL && giScene is not null)
                    {
                        giScene.Zoom(ev.wheel.y);
                    }
                    else if (type == SDL_EventType.SDL_EVENT_MOUSE_MOTION && ssrScene is not null)
                    {
                        if ((ev.motion.state & SDL_MouseButtonFlags.SDL_BUTTON_LMASK) != 0)
                            ssrScene.Drag(ev.motion.xrel, ev.motion.yrel);
                    }
                    else if (type == SDL_EventType.SDL_EVENT_MOUSE_WHEEL && ssrScene is not null)
                    {
                        ssrScene.Zoom(ev.wheel.y);
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
                else if (giScene is not null) giScene.RenderFrame();
                else if (ssrScene is not null) ssrScene.RenderFrame();
                else if (cubeScene is not null) cubeScene.RenderFrame();
                else if (computeScene is not null) computeScene.RenderFrame();
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
            // SDL creates the CAMetalLayer on the main thread. Keep the view alive until after
            // renderer/surface disposal.
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
