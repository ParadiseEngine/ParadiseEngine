using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Paradise.Features;
using Paradise.Rendering.WebGPU;
using Paradise.Windowing;
using Paradise.Windowing.Sdl;

namespace Paradise.Rendering.Sdf.Sample;

internal static class Program
{
    /// <summary>Pointer travel, in pixels, below which a press and release is a click rather
    /// than an orbit.</summary>
    private const float ClickSlop = 4f;

    /// <summary>Animation step of a headless frame, so a frame count names one image.</summary>
    private const float HeadlessStep = 1f / 60f;

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Sample failed: {error}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        FeatureSwitches switches;
        try
        {
            switches = new FeatureSwitches(FeatureOverrides.FromEnvironment().Merge(FeatureOverrides.Parse(Value(args, "--features"))));
        }
        catch (FormatException error)
        {
            return Usage($"Feature configuration: {error.Message}");
        }
        // Declared before a GPU is acquired, so listing and unknown-name checks need no adapter.
        SdfFeatures.DeclareAll(switches);
        foreach (var name in switches.Unknown)
            Console.Error.WriteLine($"Feature configuration: no feature named '{name}' in this build.");
        if (args.Contains("--list-features"))
        {
            foreach (var definition in switches.Definitions.OrderBy(d => d.Name, StringComparer.Ordinal))
                Console.WriteLine($"  {(switches.IsEnabled(definition.Id) ? "on " : "off")}  {definition.Name,-24}  {definition.Summary}");
            return 0;
        }

        uint width = 1280, height = 720;
        if (Value(args, "--size") is { } size)
        {
            if (size.Split('x') is not [var w, var h] || !uint.TryParse(w, out width) || !uint.TryParse(h, out height) || width == 0 || height == 0)
                return Usage("--size needs WIDTHxHEIGHT, such as 1280x720.");
        }
        var stats = args.Contains("--stats");
        if (!args.Contains("--headless")) return RunWindowed(switches, width, height, stats);

        if (!int.TryParse(Value(args, "--headless"), out var frames) || frames < 1)
            return Usage("--headless needs a positive frame count.");
        Vector2? pick = null;
        if (Value(args, "--pick") is { } at)
        {
            if (at.Split(',') is not [var x, var y]
                || !float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var pickX)
                || !float.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var pickY))
                return Usage("--pick needs X,Y in pixels from the top-left corner.");
            pick = new Vector2(pickX, pickY);
        }
        return RunHeadless(switches, width, height, frames, pick, Value(args, "--screenshot"), stats);
    }

    private static int RunWindowed(FeatureSwitches switches, uint width, uint height, bool stats)
    {
        // Disposal runs in reverse: the renderers go before the window whose surface they draw to.
        using var platform = new SdlWindowPlatform();
        using var window = platform.CreateWindow(new WindowOptions("Paradise.Rendering.Sdf", width, height));
        using var backend = new WebGpuRenderer(window.CreateSurface());
        using var renderer = new SdfRenderer(backend, switches, window.Width, window.Height);
        window.Resized += (w, h) =>
        {
            backend.Resize(w, h);
            renderer.Resize(w, h);
        };
        var garden = new SdfGarden();
        Console.WriteLine("Drag to orbit, scroll to zoom, click to pick. Space pause, S shadows, O ambient occlusion, T tonemap, R reset camera, Esc quit.");

        var clock = Stopwatch.StartNew();
        var previous = 0.0;
        var pointer = Vector2.Zero;
        var pressedAt = Vector2.Zero;
        var held = false;
        var sampleStart = 0.0;
        var sampleFrames = 0;
        while (!window.CloseRequested)
        {
            platform.Pump();
            while (window.TryReadEvent(out var input))
            {
                var e = input.Event;
                switch (e.Kind)
                {
                    case WindowEventKind.Button when e.Source == EventSource.Mouse && e.PointerButton == PointerButton.Left:
                        pointer = new Vector2(e.X, e.Y);
                        if (e.Pressed) pressedAt = pointer;
                        else if (held && Vector2.Distance(pointer, pressedAt) < ClickSlop)
                            Report(garden, pointer, new Vector2(window.Width, window.Height));
                        held = e.Pressed;
                        break;
                    case WindowEventKind.PointerMove when e.Source == EventSource.Mouse:
                        var next = new Vector2(e.X, e.Y);
                        if (held) garden.Drag(next.X - pointer.X, next.Y - pointer.Y);
                        pointer = next;
                        break;
                    case WindowEventKind.Scroll:
                        garden.Zoom(e.Y);
                        break;
                    case WindowEventKind.Button when e.Source == EventSource.Keyboard && e.Pressed:
                        HandleKey(e.KeyboardKey, garden, window);
                        break;
                }
            }

            var now = clock.Elapsed.TotalSeconds;
            // A stall, such as a window drag, must not fling the animation forward.
            garden.Update((float)Math.Min(now - previous, 0.1), renderer.AspectRatio);
            previous = now;
            renderer.RenderFrame(garden.Scene);

            if (!stats || ++sampleFrames < 120) continue;
            Console.WriteLine($"[stats] {(now - sampleStart) * 1000.0 / sampleFrames:F2} ms/frame at {window.Width}x{window.Height}, {garden.Scene.Shapes.Count} shapes");
            sampleStart = now;
            sampleFrames = 0;
        }
        return 0;
    }

    private static int RunHeadless(FeatureSwitches switches, uint width, uint height, int frames, Vector2? pick, string? screenshot, bool stats)
    {
        using var backend = WebGpuRenderer.CreateHeadless(width, height);
        using var renderer = new SdfRenderer(backend, switches, width, height);
        var garden = new SdfGarden();
        // Populates the scene the scripted click reads when it comes on the first frame.
        garden.Update(0f, renderer.AspectRatio);

        var clock = Stopwatch.StartNew();
        byte[] pixels = [];
        uint readWidth = 0, readHeight = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            // The scripted click lands before the last frame, which then draws its marker.
            if (pick is { } pixel && frame == frames - 1) Report(garden, pixel, new Vector2(width, height));
            garden.Update(HeadlessStep, renderer.AspectRatio);
            renderer.RenderFrame(garden.Scene);
            // Reading back waits for the GPU. Without it submissions queue faster than a slow
            // adapter draws them, and the final readback's wait covers the whole backlog.
            pixels = backend.ReadbackColor(out readWidth, out readHeight);
        }

        if (stats)
            Console.WriteLine($"[stats] {clock.Elapsed.TotalMilliseconds / frames:F2} ms/frame at {width}x{height}, {garden.Scene.Shapes.Count} shapes");
        Console.WriteLine($"Headless mode: rendered {frames} frames; passes: {string.Join(", ", renderer.LastPassNames)}.");
        if (screenshot is null) return 0;

        using var file = File.Create(screenshot);
        PngWriter.Write(file, new ColorReadback(pixels, readWidth, readHeight), backend.ColorFormat);
        Console.WriteLine($"Screenshot written to {screenshot}.");
        return 0;
    }

    private static void HandleKey(KeyboardKey key, SdfGarden garden, IWindow window)
    {
        switch (key)
        {
            case KeyboardKey.Space:
                garden.Paused = !garden.Paused;
                break;
            case KeyboardKey.S:
                garden.ToggleShadows();
                Console.WriteLine($"Shadows {(garden.Scene.Tracing.Shadows ? "on" : "off")}.");
                break;
            case KeyboardKey.O:
                garden.ToggleAmbientOcclusion();
                Console.WriteLine($"Ambient occlusion {(garden.Scene.Tracing.AmbientOcclusion > 0f ? "on" : "off")}.");
                break;
            case KeyboardKey.T:
                garden.ToggleTonemap();
                Console.WriteLine($"Tonemap {garden.Scene.Tonemap.Mode}.");
                break;
            case KeyboardKey.R:
                garden.ResetCamera();
                break;
            case KeyboardKey.Escape:
                window.RequestClose();
                break;
        }
    }

    /// <summary>Picks under a screen point and says what the CPU field found there.</summary>
    private static void Report(SdfGarden garden, Vector2 pixel, Vector2 viewport)
    {
        if (garden.TryPick(pixel, viewport, out var point, out var material))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Picked ({point.X:F2}, {point.Y:F2}, {point.Z:F2}) at pixel ({pixel.X:F0}, {pixel.Y:F0}); albedo ({material.Albedo.X:F2}, {material.Albedo.Y:F2}, {material.Albedo.Z:F2})."));
        }
        else
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Nothing under pixel ({pixel.X:F0}, {pixel.Y:F0})."));
        }
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    /// <summary>The value after <paramref name="flag"/>, or null when the flag is absent.</summary>
    private static string? Value(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
