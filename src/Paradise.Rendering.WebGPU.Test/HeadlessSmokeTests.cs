using Paradise.Windowing;
using TUnit.Core;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>Smoke tests that exercise the headless adapter path. These hit live Dawn natives via
/// WebGPUSharp; if WebGPU is not exercisable on this host the tests skip cleanly via
/// <see cref="Skip.Test(string)"/> rather than failing. Two skip conditions:
/// <list type="bullet">
/// <item><see cref="AdapterUnavailableException"/> — Dawn loaded but returned no adapter (no
/// Vulkan/Metal/DX12 backend, e.g. CI without lavapipe + libvulkan1).</item>
/// <item><see cref="DllNotFoundException"/> — Dawn's <c>webgpu_dawn</c> native (or one of its
/// transitive dependencies, notably <c>libc++.so.1</c> on Linux) cannot be loaded by the runtime.
/// Equivalent "WebGPU not available on this host" condition.</item>
/// </list>
/// Device-creation or any other backend failure surfaces as a real test failure — only
/// host-environment unavailability is treated as "not applicable here". The AOT publish in CI is
/// the load-bearing M0 acceptance signal; these are belt-and-suspenders.</summary>
public class HeadlessSmokeTests
{
    private static WebGpuRenderer? TryCreateHeadlessOrSkip(uint width, uint height)
    {
        try
        {
            return WebGpuRenderer.CreateHeadless(width, height);
        }
        catch (AdapterUnavailableException ex)
        {
            Skip.Test($"No WebGPU adapter available on this host: {ex.Message}");
            return null;
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"WebGPU native library not loadable on this host: {ex.Message}");
            return null;
        }
    }

    /// <summary>The descriptor route's twin of <see cref="TryCreateHeadlessOrSkip"/>. Same two
    /// host-unavailability conditions, because it is the same constructor underneath.</summary>
    private static WebGpuRenderer? TryCreateOrSkip(in SurfaceDescriptor surface)
    {
        try
        {
            return new WebGpuRenderer(in surface);
        }
        catch (AdapterUnavailableException ex)
        {
            Skip.Test($"No WebGPU adapter available on this host: {ex.Message}");
            return null;
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"WebGPU native library not loadable on this host: {ex.Message}");
            return null;
        }
    }

    [Test]
    public async Task headless_renderer_initializes_and_disposes()
    {
        var renderer = TryCreateHeadlessOrSkip(64, 64);
        if (renderer is null) return;

        try
        {
            await Assert.That(renderer).IsNotNull();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task headless_renderer_renders_clear_frames()
    {
        var renderer = TryCreateHeadlessOrSkip(32, 32);
        if (renderer is null) return;

        try
        {
            for (var i = 0; i < 3; i++)
                renderer.RenderClearFrame(ColorRgba.CornflowerBlue);
            await Assert.That(renderer).IsNotNull();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task headless_renderer_resize_resizes_offscreen_target()
    {
        var renderer = TryCreateHeadlessOrSkip(16, 16);
        if (renderer is null) return;

        try
        {
            renderer.Resize(128, 96);
            renderer.RenderClearFrame(ColorRgba.Black);
            await Assert.That(renderer).IsNotNull();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task readback_color_returns_tightly_packed_bgra_of_the_cleared_frame()
    {
        const uint w = 40, h = 24; // width*4 = 160 is NOT a multiple of 256 → exercises row unpadding
        var renderer = TryCreateHeadlessOrSkip(w, h);
        if (renderer is null) return;

        try
        {
            // Pure red so the BGRA channel order is unambiguous (B=0, G=0, R=255) with no rounding.
            renderer.RenderClearFrame(new ColorRgba(1f, 0f, 0f, 1f));
            var pixels = renderer.ReadbackColor(out var rw, out var rh);

            await Assert.That(rw).IsEqualTo(w);
            await Assert.That(rh).IsEqualTo(h);
            // Tightly packed: exactly width*height*4 bytes, no row padding.
            await Assert.That(pixels.Length).IsEqualTo((int)(w * h * 4));

            // Center pixel, top-down row-major, BGRA byte order.
            var idx = (int)((h / 2) * w + (w / 2)) * 4;
            await Assert.That(pixels[idx + 0]).IsLessThan((byte)4);      // B
            await Assert.That(pixels[idx + 1]).IsLessThan((byte)4);      // G
            await Assert.That(pixels[idx + 2]).IsGreaterThan((byte)251); // R
            await Assert.That(pixels[idx + 3]).IsGreaterThan((byte)251); // A
        }
        finally
        {
            renderer.Dispose();
        }
    }

    /// <summary>
    /// The surface constructor BUILDS a headless renderer from a headless descriptor, rather than
    /// refusing it.
    ///
    /// It used to throw and direct the caller to <see cref="WebGpuRenderer.CreateHeadless"/>, which
    /// left <see cref="SurfaceDescriptor"/> able to state a case the only constructor taking one
    /// would not build — so every host holding a descriptor had to know the rule and branch on it,
    /// and the branch lived in as many places as there were hosts. The descriptor is the question;
    /// this is the answer.
    /// </summary>
    [Test]
    public async Task surface_ctor_builds_a_headless_renderer_from_a_headless_descriptor()
    {
        var desc = SurfaceDescriptor.Headless(64, 32);
        var renderer = TryCreateOrSkip(in desc);
        if (renderer is null) return;

        try
        {
            // Headless for real, not merely constructed: the offscreen path reports the offscreen
            // format, and only a headless renderer permits a readback at all.
            await Assert.That(renderer.ColorFormat).IsEqualTo(TextureFormat.Bgra8Unorm);
            renderer.RenderClearFrame(new ColorRgba(0f, 0f, 0f, 1f));
            var pixels = renderer.ReadbackColor(out var width, out var height);
            await Assert.That(width).IsEqualTo(64u);
            await Assert.That(height).IsEqualTo(32u);
            await Assert.That(pixels.Length).IsEqualTo(64 * 32 * 4);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    /// <summary>A capture is the frame the renderer actually drew, delivered through a task the
    /// caller may await from anywhere. Headless here because that is what a test host can build,
    /// but the path is the shared one: the copy rides the frame's own command buffer.</summary>
    [Test]
    public async Task capture_frame_async_returns_the_frame_that_was_drawn()
    {
        var renderer = TryCreateHeadlessOrSkip(16, 8);
        if (renderer is null) return;

        try
        {
            await Assert.That(renderer.CanCaptureFrame).IsTrue();

            // Requested BEFORE the frame: the request queues and the next frame services it, which
            // is the whole shape of the API — a caller never has to know where the loop is.
            var pending = renderer.CaptureFrameAsync();
            renderer.RenderClearFrame(new ColorRgba(0f, 0f, 1f, 1f));
            var image = await pending.ConfigureAwait(false);

            await Assert.That(image.Width).IsEqualTo(16u);
            await Assert.That(image.Height).IsEqualTo(8u);
            await Assert.That(image.Pixels.Length).IsEqualTo(16 * 8 * 4);
            // BGRA: pure blue is B=255, R=0. Checks channel order as well as content.
            await Assert.That(image.Pixels[0]).IsEqualTo((byte)255);
            await Assert.That(image.Pixels[2]).IsEqualTo((byte)0);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    /// <summary>Two requests posted before a single frame are both served BY that frame, rather
    /// than one being dropped or made to wait for a second.</summary>
    [Test]
    public async Task several_requests_are_served_by_one_frame()
    {
        var renderer = TryCreateHeadlessOrSkip(8, 8);
        if (renderer is null) return;

        try
        {
            var first = renderer.CaptureFrameAsync();
            var second = renderer.CaptureFrameAsync();

            renderer.RenderClearFrame(new ColorRgba(0f, 1f, 0f, 1f));

            var a = await first.ConfigureAwait(false);
            var b = await second.ConfigureAwait(false);
            await Assert.That(a.Pixels.Length).IsEqualTo(b.Pixels.Length);
            await Assert.That(a.Pixels[1]).IsEqualTo((byte)255); // green
            await Assert.That(b.Pixels[1]).IsEqualTo((byte)255);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    /// <summary>A request with nothing rendering behind it stays pending — it is not silently
    /// completed with a stale or empty image, which would be the dangerous answer.</summary>
    [Test]
    public async Task a_request_waits_for_a_frame()
    {
        var renderer = TryCreateHeadlessOrSkip(8, 8);
        if (renderer is null) return;

        try
        {
            var pending = renderer.CaptureFrameAsync();

            var finished = await Task.WhenAny(pending, Task.Delay(250)).ConfigureAwait(false);
            await Assert.That(finished).IsNotEqualTo((Task)pending);
            await Assert.That(pending.IsCompleted).IsFalse();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    /// <summary>...but it does not stay pending FOREVER. Disposal faults what it can no longer
    /// serve, because a task nobody will ever complete is a caller hung for the life of the
    /// process — which is exactly how the missing RenderClearFrame path announced itself.</summary>
    [Test]
    public async Task disposal_faults_a_request_no_frame_will_serve()
    {
        var renderer = TryCreateHeadlessOrSkip(8, 8);
        if (renderer is null) return;

        var pending = renderer.CaptureFrameAsync();
        renderer.Dispose();

        await Assert.That(async () => await pending.ConfigureAwait(false))
            .Throws<ObjectDisposedException>();
    }

    /// <summary>
    /// A request racing disposal is never left pending.
    ///
    /// The hazard is a torn check-then-enqueue: a caller passes the disposed guard, Dispose runs to
    /// completion on another thread (setting the flag and draining what it can see), and the caller
    /// then enqueues into a queue nothing will ever look at again. That task would hang for the
    /// life of the process — the same "my await never returns" failure this capture path exists to
    /// prevent, reintroduced for the concurrent case the API advertises.
    ///
    /// Every handed-out task must SETTLE: faulted by the drain if it got in, while the call itself
    /// may legally throw if it did not. What must never happen is a task that does neither.
    ///
    /// <b>What this does NOT do is reproduce the race.</b> It was run against the unguarded version
    /// and passed there too — the window between reading the disposed flag and enqueuing is a few
    /// instructions, and disposal has to set the flag AND finish draining inside it. So this pins
    /// the POST-CONDITION rather than catching the defect; correctness rests on the enqueue and the
    /// drain sharing a lock, which is a property of the code and not of this test. Widening the
    /// window enough to catch it would mean a test hook in the production path, which costs more
    /// than it proves.
    /// </summary>
    [Test]
    public async Task a_request_racing_disposal_never_hangs()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var renderer = TryCreateHeadlessOrSkip(8, 8);
            if (renderer is null) return;

            using var start = new ManualResetEventSlim();
            var poster = Task.Run(() =>
            {
                var handed = new List<Task<ColorReadback>>();
                start.Wait();
                for (var i = 0; i < 64; i++)
                {
                    try
                    {
                        handed.Add(renderer.CaptureFrameAsync());
                    }
                    catch (ObjectDisposedException)
                    {
                        break; // the legal outcome for a call that lost the race
                    }
                }
                return handed;
            });

            start.Set();
            renderer.Dispose();
            var requested = await poster.ConfigureAwait(false);

            foreach (var task in requested)
            {
                var settled = await Task.WhenAny(task, Task.Delay(2000)).ConfigureAwait(false);
                await Assert.That(ReferenceEquals(settled, task)).IsTrue();
            }
        }
    }

    /// <summary>Asking a disposed renderer is a refusal, not a task that quietly never
    /// completes.</summary>
    [Test]
    public async Task capturing_after_disposal_throws()
    {
        var renderer = TryCreateHeadlessOrSkip(8, 8);
        if (renderer is null) return;

        renderer.Dispose();

        await Assert.That(() => renderer.CaptureFrameAsync()).Throws<ObjectDisposedException>();
    }

    /// <summary>The clear-frame path serves captures too. It presents a frame, so it owes the
    /// queue one — the bug this pins was a request queued against a renderer driven only through
    /// RenderClearFrame, which waited on a frame that never looked.</summary>
    [Test]
    public async Task the_clear_frame_path_serves_captures_as_well()
    {
        var renderer = TryCreateHeadlessOrSkip(8, 8);
        if (renderer is null) return;

        try
        {
            var pending = renderer.CaptureFrameAsync();

            renderer.RenderClearFrame(new ColorRgba(1f, 0f, 0f, 1f));

            var image = await pending.ConfigureAwait(false);
            await Assert.That(image.Pixels[2]).IsEqualTo((byte)255); // BGRA: red
        }
        finally
        {
            renderer.Dispose();
        }
    }

    /// <summary>The named factory still works and still means the same thing — it is the same
    /// constructor underneath now, which is exactly what must not regress.</summary>
    [Test]
    public async Task create_headless_agrees_with_the_descriptor_route()
    {
        var named = TryCreateHeadlessOrSkip(48, 24);
        if (named is null) return;
        var desc = SurfaceDescriptor.Headless(48, 24);
        var described = TryCreateOrSkip(in desc);
        if (described is null)
        {
            named.Dispose();
            return;
        }

        try
        {
            await Assert.That(named.ColorFormat).IsEqualTo(described.ColorFormat);
            named.RenderClearFrame(new ColorRgba(0f, 0f, 0f, 1f));
            described.RenderClearFrame(new ColorRgba(0f, 0f, 0f, 1f));
            var a = named.ReadbackColor(out var aw, out var ah);
            var b = described.ReadbackColor(out var bw, out var bh);
            await Assert.That(aw).IsEqualTo(bw);
            await Assert.That(ah).IsEqualTo(bh);
            await Assert.That(a.Length).IsEqualTo(b.Length);
        }
        finally
        {
            described.Dispose();
            named.Dispose();
        }
    }
}
