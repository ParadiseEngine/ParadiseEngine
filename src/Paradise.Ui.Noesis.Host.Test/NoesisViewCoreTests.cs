using WebGpuSharp;

namespace Paradise.Ui.Noesis.Host.Test;

/// <summary>NoesisViewCore + NoesisOverlayRenderer against real Noesis and a real (headless)
/// WebGPU adapter: the sim-thread tick must lazily create the view (applying the MVVM
/// DataContext and firing the simTick hook), input events must route into it, and the overlay
/// renderer must record a full UI frame through the render device. Tests skip when the Noesis
/// native library or a WebGPU adapter is unavailable. All view interactions happen
/// synchronously before the first await — Noesis pins each view to its creation thread, and
/// awaits may resume elsewhere.</summary>
[NotInParallel]
public class NoesisViewCoreTests
{
    private const string MinimalXaml = """
        <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              Background="Transparent">
          <Rectangle Width="120" Height="80" Fill="#FF3050C8"
                     HorizontalAlignment="Center" VerticalAlignment="Center"/>
        </Grid>
        """;

    // A minimal MVVM-shaped DataContext: Noesis registers CLR types by reflection when they
    // enter the view; a bare System.Object is not registrable ("class not registered").
    private sealed class TestViewModel
    {
        public string Title { get; set; } = "hud";
    }

    private static string WriteXamlToTempDir()
    {
        var dir = Directory.CreateTempSubdirectory("noesis-host-test").FullName;
        var path = System.IO.Path.Combine(dir, "main.xaml");
        File.WriteAllText(path, MinimalXaml);
        return path;
    }

    private static Device? TryCreateDevice()
    {
        try
        {
            var instance = WebGPU.CreateInstance();
            if (instance is null) return null;
            var options = new RequestAdapterOptions
            {
                CompatibleSurface = null!,
                PowerPreference = PowerPreference.HighPerformance,
                FeatureLevel = FeatureLevel.Core,
            };
            var adapter = instance.RequestAdapterSync(in options, 10_000_000_000UL);
            if (adapter is null) return null;
            var desc = new DeviceDescriptor
            {
                Label = "Paradise.Ui.Noesis.Host.Test",
                UncapturedErrorCallback = static (type, message) =>
                    Console.Error.WriteLine($"[NoesisHostTest][wgpu {type}] {message.ToString()}"),
            };
            return adapter.RequestDeviceSync(in desc, 10_000_000_000UL);
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"WebGPU native library not loadable on this host: {ex.Message}");
            return null;
        }
    }

    [Test]
    public async Task sim_tick_creates_the_view_and_applies_the_data_context()
    {
        var xamlPath = WriteXamlToTempDir();
        var dataContext = new TestViewModel();
        var simTicks = 0;
        var core = new NoesisViewCore(xamlPath, 320, 240, dataContext, () => simTicks++);

        var beforeCreation = core.TryUpdateRenderTree();
        try
        {
            core.Input.Tick(0.0);
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"Noesis native library not loadable on this host: {ex.Message}");
            return;
        }

        var view = core.View;
        var content = view?.Content as global::Noesis.FrameworkElement;
        var contextApplied = ReferenceEquals(content?.DataContext, dataContext);
        var resizeConsumed = core.Input.Handle(UiEvent.Resize(640f, 480f));
        _ = core.Input.Handle(UiEvent.PointerMove(10f, 10f));
        // Collect the first tick's frame before asking for a second. Tick is gated on the render
        // side having taken the last snapshot — an unmatched Update queues one nobody collects,
        // which Noesis documents as unbounded allocation — so back-to-back ticks with no render
        // between them produce ONE hook invocation, not two. See BalanceGuardTests.
        core.TryUpdateRenderTree(out _);
        core.Input.Tick(1.0 / 60.0);

        await Assert.That(beforeCreation).IsFalse();
        await Assert.That(view).IsNotNull();
        await Assert.That(contextApplied).IsTrue();
        await Assert.That(simTicks).IsEqualTo(2);
        await Assert.That(resizeConsumed).IsFalse();
        await Assert.That(core.Width).IsEqualTo(640u);
        await Assert.That(core.Height).IsEqualTo(480u);
    }

    /// <summary>Key and text UiEvents must reach the view — the events that make a Noesis menu
    /// FOCUSABLE rather than merely clickable, and which the core silently dropped until they
    /// were mapped. Asserted on the routed Noesis events (the root Grid is made focusable and
    /// focused first, because keyboard input goes to the focused element and a bare view has no
    /// theme to give anything else a template).
    ///
    /// The negative half matters as much: an unmapped UiKey must return false WITHOUT touching
    /// the view, because a host reads that false as "the game may have this key".</summary>
    [Test]
    public async Task key_and_text_events_reach_the_view_and_unmapped_keys_do_not()
    {
        var xamlPath = WriteXamlToTempDir();
        var core = new NoesisViewCore(xamlPath, 200, 100);

        try
        {
            core.Input.Tick(0.0);
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"Noesis native library not loadable on this host: {ex.Message}");
            return;
        }

        var root = (global::Noesis.FrameworkElement)core.View!.Content;
        var keyDowns = new List<global::Noesis.Key>();
        var keyUps = new List<global::Noesis.Key>();
        var text = new List<string>();
        root.KeyDown += (_, args) => keyDowns.Add(args.Key);
        root.KeyUp += (_, args) => keyUps.Add(args.Key);
        root.TextInput += (_, args) => text.Add(args.Text);

        root.Focusable = true;
        root.Focus();
        core.Input.Tick(1.0 / 60.0);

        _ = core.Input.Handle(UiEvent.KeyDown(UiKey.Enter));
        _ = core.Input.Handle(UiEvent.KeyUp(UiKey.Enter));
        _ = core.Input.Handle(UiEvent.KeyDown(UiKey.Backspace));
        _ = core.Input.Handle(UiEvent.Text('A'));

        // Unmapped: no member of UiKey maps to it, so nothing may be routed and the verdict
        // must be "not handled".
        var unmappedHandled = core.Input.Handle(UiEvent.KeyDown(UiKey.None));
        // A lone surrogate is not a character — it must not be forwarded as one.
        var surrogateHandled = core.Input.Handle(UiEvent.Text(0xD800));

        await Assert.That(keyDowns).Contains(global::Noesis.Key.Return);
        await Assert.That(keyDowns).Contains(global::Noesis.Key.Back);
        await Assert.That(keyUps).Contains(global::Noesis.Key.Return);
        await Assert.That(text).Contains("A");
        await Assert.That(unmappedHandled).IsFalse();
        await Assert.That(surrogateHandled).IsFalse();
    }

    /// <summary>A Scroll UiEvent must arrive in the view as a Noesis wheel event — one notch is
    /// 120 units — and the sub-notch deltas a MacBook trackpad reports must accumulate instead of
    /// truncating to nothing. Asserted on the routed MouseWheel event rather than a ScrollViewer's
    /// offset because a bare view has no theme, so ScrollViewer gets no template (and therefore no
    /// scroll info) here.</summary>
    [Test]
    public async Task scroll_events_reach_the_view_including_sub_notch_trackpad_deltas()
    {
        var xamlPath = WriteXamlToTempDir(); // a hit-testable Grid — the wheel needs something under it
        var core = new NoesisViewCore(xamlPath, 200, 100);

        try
        {
            core.Input.Tick(0.0);
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"Noesis native library not loadable on this host: {ex.Message}");
            return;
        }

        var deltas = new List<(int Delta, global::Noesis.Orientation Orientation)>();
        ((global::Noesis.FrameworkElement)core.View!.Content).MouseWheel +=
            (_, args) => deltas.Add((args.Delta, args.Orientation));

        // Park the pointer over the content: Noesis hit-tests a wheel event at a point, and the
        // Scroll UiEvent carries only a delta.
        _ = core.Input.Handle(UiEvent.PointerMove(100f, 50f));
        core.Input.Tick(1.0 / 60.0);

        // One whole notch down, the way a discrete mouse wheel reports it.
        _ = core.Input.Handle(UiEvent.Scroll(0f, -1f));
        // ...then twenty fractions of a notch, the way a trackpad does. Each is worth 6 units, so
        // truncating per event would lose every one of them.
        for (var i = 0; i < 20; i++)
        {
            _ = core.Input.Handle(UiEvent.Scroll(0f, -0.05f));
        }
        // ...and a horizontal notch, which must route as a horizontal wheel, not a vertical one.
        _ = core.Input.Handle(UiEvent.Scroll(1f, 0f));

        var vertical = deltas.FindAll(d => d.Orientation == global::Noesis.Orientation.Vertical);
        var horizontal = deltas.FindAll(d => d.Orientation == global::Noesis.Orientation.Horizontal);

        await Assert.That(vertical[0].Delta).IsEqualTo(-120);
        await Assert.That(vertical.ConvertAll(d => d.Delta).Sum()).IsEqualTo(-240); // the notch + 20 x 6
        await Assert.That(horizontal.Count).IsEqualTo(1);
        await Assert.That(horizontal[0].Delta).IsEqualTo(120);
    }

    [Test]
    [Arguments(WebGpuSharp.TextureFormat.RGBA8Unorm)]
    [Arguments(WebGpuSharp.TextureFormat.BGRA8Unorm)]
    public async Task frames_after_a_noesis_overlay_still_reach_the_target(WebGpuSharp.TextureFormat format)
    {
        var device = TryCreateDevice();
        if (device is null)
        {
            Skip.Test("No WebGPU adapter available.");
            return;
        }

        // Opacity group => RenderOffscreen records into an offscreen surface first — the same
        // path the game overlay exercises (and the launcher-freeze suspect).
        const string opacityXaml = """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  Background="Transparent">
              <Grid Opacity="0.6" Width="80" Height="60"
                    HorizontalAlignment="Center" VerticalAlignment="Center">
                <Rectangle Fill="#FF3050C8"/>
                <Ellipse Fill="#FFFFD34E" Margin="20,10"/>
              </Grid>
            </Grid>
            """;
        var dir = Directory.CreateTempSubdirectory("noesis-host-test-opacity").FullName;
        var xamlPath = System.IO.Path.Combine(dir, "main.xaml");
        File.WriteAllText(xamlPath, opacityXaml);
        var core = new NoesisViewCore(xamlPath, 128, 128);
        var overlay = new NoesisOverlayRenderer(core, device, format);
        try
        {
            core.Input.Tick(0.0);
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"Noesis native library not loadable on this host: {ex.Message}");
            return;
        }

        var target = device.CreateTexture(new TextureDescriptor
        {
            Label = "NoesisHostTest.FrozenTarget",
            Size = new Extent3D(128, 128, 1),
            Format = format,
            Usage = TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            MipLevelCount = 1,
            SampleCount = 1,
            Dimension = TextureDimension.D2,
        })!;
        var targetView = target.CreateView()!;
        var queue = device.GetQueue()!;

        void SubmitFrame(double r, double g, double b, bool withOverlay)
        {
            var encoder = device.CreateCommandEncoder()!;
            var colors = new RenderPassColorAttachment[]
            {
                new()
                {
                    View = targetView,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearValue = new WebGpuSharp.Color(r, g, b, 1.0),
                    DepthSlice = null,
                },
            };
            var desc = new RenderPassDescriptor { ColorAttachments = colors };
            encoder.BeginRenderPass(in desc).End();
            if (withOverlay) overlay.RecordOverlay(encoder, targetView);
            queue.Submit(encoder.Finish()!);
            queue.OnSubmittedWorkSync(5_000_000_000UL);
        }

        // Reads (corner green, center green): byte 1 is G in both RGBA8 and BGRA8.
        // Corner = clear color probe; center = Noesis content probe (the XAML's centered
        // opacity group draws a blue rect + yellow ellipse there — either way G differs
        // sharply from a pure red or pure green clear).
        (byte CornerG, byte CornerA, byte CenterG) ReadPixels()
        {
            const uint padded = 512; // 128 * 4
            var buffer = device.CreateBuffer(new BufferDescriptor
            {
                Label = "NoesisHostTest.FrozenReadback",
                Size = padded * 128,
                Usage = BufferUsage.MapRead | BufferUsage.CopyDst,
                MappedAtCreation = false,
            })!;
            var enc = device.CreateCommandEncoder()!;
            var src = new TexelCopyTextureInfo { Texture = target, MipLevel = 0 };
            var dst = new TexelCopyBufferInfo
            {
                Buffer = buffer,
                Layout = new TexelCopyBufferLayout { Offset = 0, BytesPerRow = padded, RowsPerImage = 128 },
            };
            var extent = new Extent3D(128, 128, 1);
            enc.CopyTextureToBuffer(in src, in dst, in extent);
            queue.Submit(enc.Finish()!);
            queue.OnSubmittedWorkSync(5_000_000_000UL);
            byte corner = 0, cornerA = 0, center = 0;
            buffer.MapSync(MapMode.Read, 0, padded * 128, 5_000);
            buffer.GetConstMappedRange(0, padded * 128, (ReadOnlySpan<byte> mapped) =>
            {
                corner = mapped[1];
                cornerA = mapped[3]; // alpha: 255 when any opaque clear landed, 0 on a never-written texture
                center = mapped[(int)(64 * padded + 64 * 4 + 1)];
            });
            buffer.Unmap();
            return (corner, cornerA, center);
        }

        // Frame 1: red clear + the Noesis overlay (corner pixel stays red — the XAML content is
        // centered and does not cover it; the CENTER pixel must show Noesis content, not the
        // red clear). Frame 2: green clear only. If the overlay frame poisons subsequent
        // submissions, frame 2's clear never lands and the corner still reads red — the
        // launcher freeze reproduced in isolation.
        SubmitFrame(1.0, 0.0, 0.0, withOverlay: true);
        var afterOverlayFrame = ReadPixels();
        SubmitFrame(0.0, 1.0, 0.0, withOverlay: false);
        var afterPlainFrame = ReadPixels();

        await Assert.That((int)afterOverlayFrame.CornerG).IsLessThan(30);      // red, not green
        await Assert.That((int)afterOverlayFrame.CornerA).IsGreaterThan(220);  // ...and actually landed
        // Center over red clear: pure red has G=0; the blue rect / yellow ellipse both have G far
        // from 0 (Fill #FF3050C8 → G=0x50, #FFFFD34E → G=0xD3). Noesis content must be present.
        await Assert.That((int)afterOverlayFrame.CenterG).IsGreaterThan(48);
        await Assert.That((int)afterPlainFrame.CornerG).IsGreaterThan(220);  // follow-up frame landed
        await Assert.That(overlay.Device!.Unsupported).IsEmpty();
    }

    [Test]
    [Arguments(WebGpuSharp.TextureFormat.BGRA8Unorm)]
    public async Task concurrent_sim_ticks_do_not_freeze_the_target(WebGpuSharp.TextureFormat format)
    {
        var device = TryCreateDevice();
        if (device is null)
        {
            Skip.Test("No WebGPU adapter available.");
            return;
        }

        const string opacityXaml = """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  Background="Transparent">
              <Grid Opacity="0.6" Width="80" Height="60"
                    HorizontalAlignment="Center" VerticalAlignment="Center">
                <Rectangle Fill="#FF3050C8"/>
                <Ellipse Fill="#FFFFD34E" Margin="20,10"/>
              </Grid>
            </Grid>
            """;
        var dir = Directory.CreateTempSubdirectory("noesis-host-test-threads").FullName;
        var xamlPath = System.IO.Path.Combine(dir, "main.xaml");
        File.WriteAllText(xamlPath, opacityXaml);
        var core = new NoesisViewCore(xamlPath, 128, 128);
        var overlay = new NoesisOverlayRenderer(core, device, format);

        // Sim thread: creates the view on its own thread (pinning its dispatcher there, like
        // the game's 60 Hz sim thread) and keeps updating it while the main thread renders.
        Exception? simError = null;
        using var stop = new CancellationTokenSource();
        var sim = new Thread(() =>
        {
            try
            {
                var t = 0.0;
                while (!stop.IsCancellationRequested)
                {
                    core.Input.Tick(t);
                    t += 1.0 / 60.0;
                    Thread.Sleep(16);
                }
            }
            catch (Exception ex)
            {
                simError = ex;
            }
        })
        { IsBackground = true, Name = "test-sim" };
        sim.Start();

        var target = device.CreateTexture(new TextureDescriptor
        {
            Label = "NoesisHostTest.ThreadTarget",
            Size = new Extent3D(128, 128, 1),
            Format = format,
            Usage = TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            MipLevelCount = 1,
            SampleCount = 1,
            Dimension = TextureDimension.D2,
        })!;
        var targetView = target.CreateView()!;
        var queue = device.GetQueue()!;

        void SubmitFrame(double r, double g, double b, bool withOverlay)
        {
            var encoder = device.CreateCommandEncoder()!;
            var colors = new RenderPassColorAttachment[]
            {
                new()
                {
                    View = targetView,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearValue = new WebGpuSharp.Color(r, g, b, 1.0),
                    DepthSlice = null,
                },
            };
            var desc = new RenderPassDescriptor { ColorAttachments = colors };
            encoder.BeginRenderPass(in desc).End();
            if (withOverlay) overlay.RecordOverlay(encoder, targetView);
            queue.Submit(encoder.Finish()!);
        }

        (byte CornerG, byte CenterG) ReadPixels()
        {
            const uint padded = 512;
            var buffer = device.CreateBuffer(new BufferDescriptor
            {
                Label = "NoesisHostTest.ThreadReadback",
                Size = padded * 128,
                Usage = BufferUsage.MapRead | BufferUsage.CopyDst,
                MappedAtCreation = false,
            })!;
            var enc = device.CreateCommandEncoder()!;
            var src = new TexelCopyTextureInfo { Texture = target, MipLevel = 0 };
            var dst = new TexelCopyBufferInfo
            {
                Buffer = buffer,
                Layout = new TexelCopyBufferLayout { Offset = 0, BytesPerRow = padded, RowsPerImage = 128 },
            };
            var extent = new Extent3D(128, 128, 1);
            enc.CopyTextureToBuffer(in src, in dst, in extent);
            queue.Submit(enc.Finish()!);
            queue.OnSubmittedWorkSync(5_000_000_000UL);
            byte corner = 0, center = 0;
            buffer.MapSync(MapMode.Read, 0, padded * 128, 5_000);
            buffer.GetConstMappedRange(0, padded * 128, (ReadOnlySpan<byte> mapped) =>
            {
                corner = mapped[1];
                center = mapped[(int)(64 * padded + 64 * 4 + 1)];
            });
            buffer.Unmap();
            return (corner, center);
        }

        // 120 red frames with the overlay while the sim thread updates concurrently (the
        // launcher's exact threading). The centered XAML content must be visible in the last
        // overlay frame, and a follow-up green frame must still land.
        for (var i = 0; i < 120; i++)
        {
            SubmitFrame(1.0, 0.0, 0.0, withOverlay: true);
            Thread.Sleep(4);
        }
        var lastOverlayFrame = ReadPixels();
        SubmitFrame(0.0, 1.0, 0.0, withOverlay: false);
        var afterPlainFrame = ReadPixels();
        stop.Cancel();
        sim.Join(2000);

        await Assert.That(simError).IsNull();
        await Assert.That((int)lastOverlayFrame.CornerG).IsLessThan(30);      // red clear landed
        await Assert.That((int)lastOverlayFrame.CenterG).IsGreaterThan(48);   // Noesis content present
        await Assert.That((int)afterPlainFrame.CornerG).IsGreaterThan(220);   // follow-up frame landed
        await Assert.That(overlay.Device!.Unsupported).IsEmpty();
    }

    [Test]
    public async Task overlay_renderer_records_the_ui_passes_into_a_frame()
    {
        var device = TryCreateDevice();
        if (device is null)
        {
            Skip.Test("No WebGPU adapter available.");
            return;
        }

        var xamlPath = WriteXamlToTempDir();
        var core = new NoesisViewCore(xamlPath, 256, 256);
        var overlay = new NoesisOverlayRenderer(core, device, WebGpuSharp.TextureFormat.RGBA8Unorm);

        try
        {
            core.Input.Tick(0.0);
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"Noesis native library not loadable on this host: {ex.Message}");
            return;
        }

        var target = device.CreateTexture(new TextureDescriptor
        {
            Label = "NoesisHostTest.Target",
            Size = new Extent3D(256, 256, 1),
            Format = WebGpuSharp.TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderAttachment,
            MipLevelCount = 1,
            SampleCount = 1,
            Dimension = TextureDimension.D2,
        })!;
        var targetView = target.CreateView()!;
        var queue = device.GetQueue()!;

        var encoder = device.CreateCommandEncoder()!;
        overlay.RecordOverlay(encoder, targetView);
        queue.Submit(encoder.Finish()!);
        queue.OnSubmittedWorkSync(5_000_000_000UL);

        await Assert.That(overlay.Device).IsNotNull();
        await Assert.That(overlay.Device!.Unsupported).IsEmpty();
    }
}
