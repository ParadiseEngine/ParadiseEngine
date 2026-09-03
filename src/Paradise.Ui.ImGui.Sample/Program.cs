using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Paradise.Rendering.WebGPU;
using Paradise.Rendering;
using Paradise.Windowing;
using Paradise.Windowing.Sdl;
using Zio.FileSystems;

namespace Paradise.Ui.ImGui.Sample;

/// <summary>The ImGui stack end to end, through the seams a real host uses: an
/// <see cref="IWindow"/> for input, <c>WebGpuRenderer.OverlayPass</c> for composition, and a
/// system-font mount for glyphs.
///
/// <code>
/// dotnet run --project src/Paradise.Ui.ImGui.Sample                      # windowed
/// dotnet run --project src/Paradise.Ui.ImGui.Sample -- --capture ui.png  # offscreen, writes a PNG
/// </code>
///
/// The capture mode exists so this is verifiable without a display — and without a human. It
/// renders a fixed number of frames offscreen and writes the last one out, which is the only way
/// to check that what the texture protocol uploaded actually LOOKS like text.
///
/// Sim and render run on one thread here, which is both the simplest thing a sample can do and
/// the configuration the editor uses. The handoff still goes through
/// <see cref="ImGuiFrameExchange"/> exactly as it would across two.</summary>
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

        // ImGui persists window layout to "imgui.ini" in the WORKING DIRECTORY by default, which
        // for a sample run from the repo means dropping a file in the repo. Off here so a run
        // leaves nothing behind — and so the capture is reproducible rather than restoring
        // whatever size the window was dragged to last time. A host that wants remembered layout
        // sets a path of its own choosing instead.
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

    /// <summary>Stands in for the scene: one pass that clears the backbuffer, so the overlay has
    /// something to composite over and the frame goes through <c>Submit</c> — the path that runs
    /// <c>OverlayPass</c> and presents. <c>RenderClearFrame</c> deliberately does neither.
    ///
    /// The pass has to be RECORDED, not merely described. A stream carrying the descriptor table
    /// and no commands submits nothing at all: the first capture off this sample came out with an
    /// untouched backbuffer behind the UI, and a red clear colour proved it by not appearing.</summary>
    private sealed class ClearFrame
    {
        private readonly ArrayBufferWriter<RenderCommand> _commands = new(2);
        private readonly RenderPassDesc[] _passes;

        public ClearFrame(ColorRgba color)
        {
            _passes = new RenderPassDesc[1];
            _passes[0] = new RenderPassDesc(colorAttachmentCount: 1);
            _passes[0].Colors.Slot0 = new ColorAttachmentDesc(
                View: RenderViewHandle.Invalid, // backbuffer
                Load: LoadOp.Clear,
                Store: StoreOp.Store,
                ClearValue: color);
        }

        public RenderCommandStream Record()
        {
            _commands.ResetWrittenCount();
            var encoder = new RenderCommandEncoder(_commands);
            encoder.BeginPass(0);
            encoder.EndPass();
            return new RenderCommandStream(_commands.WrittenMemory, _passes);
        }
    }
}
