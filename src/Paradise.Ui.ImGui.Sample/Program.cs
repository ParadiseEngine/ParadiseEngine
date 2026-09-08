using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Paradise.Rendering.WebGPU;
using Paradise.Rendering;
using Paradise.Windowing;
using Paradise.Windowing.Sdl;
using Zio.FileSystems;

namespace Paradise.Ui.ImGui.Sample;

/// <summary>Demonstrates ImGui input, texture transfer and WebGPU overlay rendering.</summary>
/// <remarks>Run normally for a window, or with --capture ui.png for an offscreen PNG. Simulation
/// and rendering share a thread but use the same ImGuiFrameExchange as a threaded host.</remarks>
internal static class Program
{
    private const uint Width = 1280;
    private const uint Height = 800;
    private const float FontSize = 18f;

    private static int Main(string[] args)
    {
        var capturePath = ValueAfter(args, "--capture");
        var frames = int.TryParse(ValueAfter(args, "--frames"), out var parsed) ? parsed : 8;
        try
        {
            return capturePath is null ? RunWindowed() : RunCapture(capturePath, frames);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Sample failed: {ex}");
            return 1;
        }
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : null;
    }

    /// <summary>Build the core over the best CJK font this machine has, falling back to ImGui's
    /// ASCII-only default. The mount is the host's to own, so it outlives the core and is disposed
    /// by the caller.</summary>
    private static unsafe ImGuiUiCore CreateCore(uint width, uint height, out AggregateFileSystem fonts, out string description)
    {
        var host = new PhysicalFileSystem();
        fonts = UiFonts.MountSystemFonts(host);
        var font = UiFonts.FindCjkFont(fonts, FontSize);
        description = font is null
            ? "default font (ASCII only) — no CJK-capable system font found"
            : $"font: {font.Path} @ {FontSize}px";
        Console.WriteLine($"[sample] {description}");
        var core = new ImGuiUiCore(width, height, font);

        // Disable imgui.ini so sample runs leave no file and captures do not restore previous
        // window positions.
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        io.IniFilename = null;
        return core;
    }

    private static int RunWindowed()
    {
        using var platform = new SdlWindowPlatform();
        using var window = platform.CreateWindow(new WindowOptions("Paradise.Ui.ImGui", Width, Height));
        using var renderer = new WebGpuRenderer(window.CreateSurface());

        using var core = CreateCore(window.Width, window.Height, out var fonts, out var description);
        using var overlay = new ImGuiWebGpuRenderer(renderer.NativeDevice, renderer.NativeColorFormat);
        var panels = new SamplePanels(description);
        core.AddDraw(panels.Draw);

        window.Resized += (w, h) => renderer.Resize(w, h);

        var pending = new List<ImGuiTextureOp>();
        var scene = new ClearFrame(new ColorRgba(0.08f, 0.09f, 0.11f, 1f));
        var clock = Stopwatch.StartNew();
        while (!window.CloseRequested)
        {
            platform.Pump();
            // The UI sees the window's own event vocabulary, unmodified — deciding WHICH events it
            // sees is the host's job, and a debug overlay wants all of them.
            while (window.TryReadEvent(out var input)) core.Input.Handle(input.Event);
            core.Input.Tick(clock.Elapsed.TotalSeconds);

            var snapshot = core.AcquireSnapshotForRender(pending, out _);
            renderer.OverlayPass = (encoder, view) =>
            {
                overlay.ApplyTextureOps(pending);
                if (snapshot is not null) overlay.Render(encoder, view, window.Width, window.Height, snapshot);
            };
            renderer.Submit(scene.Record());
        }

        fonts.Dispose();
        return 0;
    }

    private static int RunCapture(string path, int frames)
    {
        using var platform = new SdlWindowPlatform(); // SDL still initializes; no window is opened.
        using var renderer = WebGpuRenderer.CreateHeadless(Width, Height);
        if (!renderer.CanCaptureFrame) throw new InvalidOperationException("Headless target cannot be captured.");

        using var core = CreateCore(Width, Height, out var fonts, out var description);
        using var overlay = new ImGuiWebGpuRenderer(renderer.NativeDevice, renderer.NativeColorFormat);
        var panels = new SamplePanels(description);
        core.AddDraw(panels.Draw);

        var pending = new List<ImGuiTextureOp>();
        var scene = new ClearFrame(new ColorRgba(0.08f, 0.09f, 0.11f, 1f));
        Task<ColorReadback>? capture = null;
        for (var frame = 0; frame < frames; frame++)
        {
            // Requested BEFORE the last frame, because the NEXT presented frame is what serves a
            // capture — asking afterwards and blocking would wait for a frame nobody will draw.
            if (frame == frames - 1) capture = renderer.CaptureFrameAsync();

            core.Input.Tick(frame / 60.0);
            var snapshot = core.AcquireSnapshotForRender(pending, out _);
            renderer.OverlayPass = (encoder, view) =>
            {
                overlay.ApplyTextureOps(pending);
                if (snapshot is not null) overlay.Render(encoder, view, Width, Height, snapshot);
            };
            renderer.Submit(scene.Record());
        }

        // The last frame only: the first has an atlas that is still arriving.
        var readback = capture!.GetAwaiter().GetResult();
        using (var file = System.IO.File.Create(path))
        {
            PngWriter.Write(file, readback, renderer.ColorFormat);
        }
        Console.WriteLine($"[sample] {frames} frames rendered; wrote {path} ({readback.Width}x{readback.Height}).");
        fonts.Dispose();
        return 0;
    }
}
