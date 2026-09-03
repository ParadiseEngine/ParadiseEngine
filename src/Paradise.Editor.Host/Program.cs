using Microsoft.Extensions.Logging;
using Paradise.Diagnostics;
using Paradise.Editor.Core.Persistence;
using Paradise.Editor.ImGui;
using Paradise.Editor.ImGui.Shell;
using Paradise.Rendering;
using Paradise.Rendering.WebGPU;
using Paradise.Ui.ImGui;
using Paradise.Windowing;
using Paradise.Windowing.Sdl;
using Zio;
using Zio.FileSystems;

namespace Paradise.Editor.Host;

/// <summary>The standalone editor: an SDL window, a WebGPU device, and the ImGui frame the editor
/// draws into.
///
/// <code>
/// dotnet run --project src/Paradise.Editor.Host                                   # windowed
/// dotnet run --project src/Paradise.Editor.Host -- --frames 8 --screenshot ed.png # headless
/// </code>
///
/// The headless mode is what makes this verifiable in CI, where there is no display and no
/// person: it renders offscreen, checks the frame actually produced draw commands, and writes the
/// last one out as a PNG somebody can look at when it goes wrong.
///
/// Both halves of the ImGui core run on THIS thread. The two-half split exists for a game whose
/// sim and render threads differ; an editor has one loop, and the handoff through
/// <see cref="ImGuiFrameExchange"/> is identical either way.</summary>
internal static class Program
{
    private const uint Width = 1600;
    private const uint Height = 1000;
    private const float FontSize = EditorFonts.DefaultSizePixels;
    private static readonly ColorRgba Background = new(0.08f, 0.09f, 0.11f, 1f);

    private static ILoggerFactory s_log = ParadiseConsole.CreateFactory(new ParadiseConsoleOptions());

    private static int Main(string[] args)
    {
        if (ParseLogLevel(args) is not { } level) return 1;
        s_log = ParadiseConsole.CreateFactory(new ParadiseConsoleOptions { MinLevel = level });

        if (ParseScreenshot(args) is not { } screenshot) return 1;
        if (ParseFrames(args) is not { } frames) return 1;

        try
        {
            return screenshot.Length == 0 ? RunWindowed() : RunHeadless(screenshot, frames);
        }
        catch (Exception exception)
        {
            s_log.CreateLogger("Paradise.Editor.Host").LogCritical(exception, "the editor could not start");
            return 1;
        }
        finally
        {
            s_log.Dispose();
        }
    }

    private static int RunWindowed()
    {
        using var platform = new SdlWindowPlatform(s_log.CreateLogger<SdlWindowPlatform>());
        using var window = platform.CreateWindow(new WindowOptions("Paradise Editor", Width, Height));
        using var renderer = new WebGpuRenderer(
            window.CreateSurface(), logger: s_log.CreateLogger<WebGpuRenderer>());

        using var fonts = UiFonts.MountSystemFonts(new PhysicalFileSystem());
        using var core = CreateCore(window.Width, window.Height, fonts);
        using var overlay = new ImGuiWebGpuRenderer(renderer.NativeDevice, renderer.NativeColorFormat);
        using var userMount = UserMount();
        using var editor = new EditorFrame(new WorkspaceLayoutStore(core, userMount), s_log.CreateLogger("Paradise.Editor"));
        core.AddDraw(editor.Draw);

        var pending = new List<ImGuiTextureOp>();
        var scene = new ClearFrame(Background);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!window.CloseRequested)
        {
            platform.Pump();
            while (window.TryReadEvent(out var input))
            {
                // Resize is taken from the STREAM rather than from IWindow.Resized, so the
                // swapchain and ImGui's display size change at the same point in the sequence the
                // pointer events do. Handled off the event instead of the callback, a click that
                // arrives in the same pump as a resize is hit-tested against the size it was
                // actually made at.
                if (input.Event.Kind == WindowEventKind.Resize)
                {
                    renderer.Resize((uint)input.Event.X, (uint)input.Event.Y);
                }
                core.Input.Handle(input.Event);
            }
            core.Input.Tick(clock.Elapsed.TotalSeconds);

            var snapshot = core.AcquireSnapshotForRender(pending, out _);
            renderer.OverlayPass = (encoder, view) =>
            {
                overlay.ApplyTextureOps(pending);
                if (snapshot is not null) overlay.Render(encoder, view, window.Width, window.Height, snapshot);
            };
            renderer.Submit(scene.Record());
            editor.SaveLayoutIfChanged(core.WantSaveLayout);
        }

        // The arrangement at the moment of closing is the one to restore, not the one from the
        // last time ImGui's save timer happened to fire.
        editor.Layout.Save();
        return 0;
    }

    private static int RunHeadless(string screenshot, int frames)
    {
        var log = s_log.CreateLogger("Paradise.Editor.Host");
        // No SdlWindowPlatform: headless needs no window, no input pump and no display, and this
        // is the mode CI runs. Initializing SDL here would put a video driver in the way of a
        // check that is only about the renderer and the UI — on a hosted runner there is none,
        // and the smoke would fail for a reason that has nothing to do with the editor.
        using var renderer = WebGpuRenderer.CreateHeadless(Width, Height, s_log.CreateLogger<WebGpuRenderer>());
        if (!renderer.CanCaptureFrame) throw new InvalidOperationException("Headless target cannot be captured.");

        using var fonts = UiFonts.MountSystemFonts(new PhysicalFileSystem());
        using var core = CreateCore(Width, Height, fonts);
        using var overlay = new ImGuiWebGpuRenderer(renderer.NativeDevice, renderer.NativeColorFormat);
        // No layout store: a smoke run must not read a developer's arrangement or leave one behind.
        using var editor = new EditorFrame(log: log);
        core.AddDraw(editor.Draw);

        var pending = new List<ImGuiTextureOp>();
        var scene = new ClearFrame(Background);
        var commands = 0;
        Task<ColorReadback>? capture = null;
        for (var frame = 0; frame < frames; frame++)
        {
            // Requested BEFORE the last frame: the NEXT presented frame serves a capture, so
            // asking afterwards would wait for one nobody is going to draw.
            if (frame == frames - 1) capture = renderer.CaptureFrameAsync();

            core.Input.Tick(frame / 60.0);
            var snapshot = core.AcquireSnapshotForRender(pending, out _);
            commands = snapshot?.CommandCount ?? 0;
            renderer.OverlayPass = (encoder, view) =>
            {
                overlay.ApplyTextureOps(pending);
                if (snapshot is not null) overlay.Render(encoder, view, Width, Height, snapshot);
            };
            renderer.Submit(scene.Record());
        }

        // The two things a smoke run can be wrong about, and they fail differently: a UI that
        // drew nothing still produces a valid PNG of the clear colour, and a UI that drew
        // everything still writes nothing if the capture path is broken. Both are checked.
        if (commands == 0) throw new InvalidOperationException("The editor frame produced no draw commands.");
        if (editor.Layout.ActiveNode == 0) throw new InvalidOperationException("The dockspace node was never built.");

        var readback = capture!.GetAwaiter().GetResult();
        using (var file = File.Create(screenshot))
        {
            PngWriter.Write(file, readback, renderer.ColorFormat);
        }

        log.LogInformation(
            "{Frames} frames, {Commands} draw commands in the last; wrote {Path} ({Width}x{Height})",
            frames, commands, screenshot, readback.Width, readback.Height);
        return 0;
    }

    /// <summary>Build the core over the editor's embedded faces, merging a CJK face out of
    /// <paramref name="systemFonts"/> when the machine has one.</summary>
    /// <remarks>The system mount is the caller's so it can be a <c>using</c> and therefore survive
    /// an exception. The embedded mount is not: it only has to outlive the calls that read bytes
    /// out of it, since ImGui copies them into its own allocation.</remarks>
    private static ImGuiUiCore CreateCore(uint width, uint height, IFileSystem systemFonts)
    {
        // The editor's own faces, embedded. Inter goes in as the BASE font because ImGui treats
        // the first in the atlas as the default; the icons and a system CJK face merge onto it, so
        // one font covers text, icons and CJK and no panel ever switches fonts mid-line.
        using var embedded = EditorFonts.Mount();
        var core = new ImGuiUiCore(width, height, EditorFonts.Base(embedded, FontSize));
        EditorFonts.MergeIcons(embedded, FontSize);
        EditorFonts.MergeSystemCjk(systemFonts, FontSize);

        // E1 owns layout persistence through the /user mount. Until then the ini is off rather
        // than left at ImGui's default, which writes into the process's working directory — for a
        // `dotnet run` from the repo, into the repo.
        core.DisableIniFile();
        EditorDockspace.EnableDocking();
        EditorTheme.Apply();
        return core;
    }

    /// <summary>The <c>/user</c> mount: this machine's config directory, and nothing above it.</summary>
    /// <remarks>A <c>SubFileSystem</c> rather than a physical root, so a path that climbs out of
    /// the editor's own directory throws instead of resolving somewhere in the user's home. The
    /// containment is the mount's job, not a check written at each call site.</remarks>
    private static SubFileSystem UserMount()
    {
        var physical = new PhysicalFileSystem();
        var root = physical.ConvertPathFromInternal(
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
                "ParadiseEditor"));
        if (!physical.DirectoryExists(root)) physical.CreateDirectory(root);
        return new SubFileSystem(physical, root, owned: true);
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : null;
    }

    /// <summary>The screenshot path, or empty for windowed. Null means the argument was bad and
    /// was reported.</summary>
    /// <remarks>A bare <c>--screenshot</c>, or one whose value was swallowed by the next flag,
    /// must NOT fall through to the windowed path. On a CI runner that boots SDL with no display
    /// and fails somewhere that says nothing about the real mistake — which is exactly the
    /// misdiagnosis the smoke workflow is arranged to avoid.</remarks>
    private static string? ParseScreenshot(string[] args)
    {
        if (Array.IndexOf(args, "--screenshot") < 0) return string.Empty;
        if (ValueAfter(args, "--screenshot") is not { Length: > 0 } path)
        {
            Console.Error.WriteLine("Usage: --screenshot <path>");
            return null;
        }
        return path;
    }

    /// <summary><c>--frames N</c>, or 8. Null means the argument was bad and was reported.</summary>
    private static int? ParseFrames(string[] args)
    {
        if (Array.IndexOf(args, "--frames") < 0) return 8;
        if (ValueAfter(args, "--frames") is not { } text || !int.TryParse(text, out var frames) || frames <= 0)
        {
            Console.Error.WriteLine("Usage: --frames <positive integer>");
            return null;
        }
        return frames;
    }

    private static LogLevel? ParseLogLevel(string[] args)
    {
        if (Array.IndexOf(args, "--log-level") < 0) return LogLevel.Information;
        if (ValueAfter(args, "--log-level") is not { } text
            || !Enum.TryParse<LogLevel>(text, ignoreCase: true, out var level))
        {
            Console.Error.WriteLine($"Usage: --log-level <{string.Join('|', Enum.GetNames<LogLevel>())}>");
            return null;
        }
        return level;
    }
}
