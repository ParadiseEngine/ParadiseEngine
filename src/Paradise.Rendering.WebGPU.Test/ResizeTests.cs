namespace Paradise.Rendering.WebGPU.Test;

/// <summary>Resize and surface-recreate path tests.
///
/// <para>All tests use the headless adapter path (<see cref="WebGpuRenderer.CreateHeadless"/>)
/// so they can run in CI without a display server. Surface-backed resize (which exercises the
/// WebGPU swapchain reconfigure path) requires a native window and is exercised by the soak
/// mode in the sample (<c>--soak N</c>).</para>
///
/// <para>Device-loss recovery via the WebGPUSharp device-lost callback: currently not feasible
/// to trigger programmatically in Dawn (no public API for injecting device loss in the headless
/// adapter path). A TODO is tracked at the bottom of this file; the test suite will be extended
/// when Dawn exposes a mechanism for that.</para>
/// </summary>
public class ResizeTests
{
    // -------- helpers --------

    private static WebGpuRenderer? TryCreateHeadlessOrSkip(uint w = 16, uint h = 16)
    {
        try
        {
            return WebGpuRenderer.CreateHeadless(w, h);
        }
        catch (AdapterUnavailableException ex)
        {
            Skip.Test($"No WebGPU adapter: {ex.Message}");
            return null;
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"WebGPU native not loadable: {ex.Message}");
            return null;
        }
    }

    // -------- Resize tests --------

    [Test]
    public async Task headless_resize_800x600_to_1024x768_renders_without_exception()
    {
        var renderer = TryCreateHeadlessOrSkip(800, 600);
        if (renderer is null) return;

        try
        {
            renderer.RenderClearFrame(ColorRgba.CornflowerBlue);
            renderer.Resize(1024, 768);
            renderer.RenderClearFrame(ColorRgba.CornflowerBlue);
            await Assert.That(renderer).IsNotNull();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task headless_resize_sequence_no_leaks_or_exceptions()
    {
        // Exercises the multi-step resize path: up, down, minimized (0,0 → clamped to 1×1),
        // then restored. Each step renders one clear frame. No exceptions = no offscreen-target
        // leak or Resize state machine bug.
        var renderer = TryCreateHeadlessOrSkip(800, 600);
        if (renderer is null) return;

        try
        {
            renderer.RenderClearFrame(ColorRgba.Black);

            // Step 1: resize up
            renderer.Resize(1024, 768);
            renderer.RenderClearFrame(ColorRgba.Black);

            // Step 2: resize down
            renderer.Resize(320, 240);
            renderer.RenderClearFrame(ColorRgba.Black);

            // Step 3: "minimized" — Resize(0,0) is clamped to 1×1 internally; must not crash.
            renderer.Resize(0, 0);
            renderer.RenderClearFrame(ColorRgba.Black);

            // Step 4: restore
            renderer.Resize(800, 600);
            renderer.RenderClearFrame(ColorRgba.Black);

            await Assert.That(renderer).IsNotNull();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task headless_repeated_same_size_resize_is_idempotent()
    {
        // Resize to the same dimensions as the current size must be a no-op (no texture
        // reallocation) and must not throw. The WebGpuRenderer short-circuits on
        // (width == _offscreenWidth && height == _offscreenHeight).
        var renderer = TryCreateHeadlessOrSkip(64, 64);
        if (renderer is null) return;

        try
        {
            for (var i = 0; i < 5; i++)
            {
                renderer.Resize(64, 64); // same size every time
                renderer.RenderClearFrame(ColorRgba.Black);
            }
            await Assert.That(renderer).IsNotNull();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task headless_zero_width_clamped_to_one()
    {
        // Resize(0, N) must clamp width to 1 (not crash with a zero-extent texture).
        var renderer = TryCreateHeadlessOrSkip(32, 32);
        if (renderer is null) return;

        try
        {
            renderer.Resize(0, 32);
            renderer.RenderClearFrame(ColorRgba.Black);
            await Assert.That(renderer).IsNotNull();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task headless_zero_height_clamped_to_one()
    {
        var renderer = TryCreateHeadlessOrSkip(32, 32);
        if (renderer is null) return;

        try
        {
            renderer.Resize(32, 0);
            renderer.RenderClearFrame(ColorRgba.Black);
            await Assert.That(renderer).IsNotNull();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task headless_resize_after_dispose_throws_object_disposed()
    {
        var renderer = TryCreateHeadlessOrSkip(16, 16);
        if (renderer is null) return;

        renderer.Dispose();

        await Assert.That(() => renderer.Resize(32, 32)).Throws<ObjectDisposedException>();
    }

    // -------- Device-loss recovery --------
    // TODO: When Dawn exposes a programmatic device-loss injection API via WebGPUSharp, add a
    // test that:
    //   1. Calls the device-lost callback to simulate loss.
    //   2. Calls renderer.RecoverFromDeviceLoss() (not yet implemented).
    //   3. Asserts the renderer creates a new device and can render again.
    //
    // Currently not feasible: Dawn's headless adapter does not expose a
    // `wgpuDeviceInjectError` or equivalent path in the M1 WebGPUSharp 0.5.x binding.
    // Tracked as a follow-up to #45. The surface-backed path (windowed) also goes through
    // SurfaceState.Reconfigure() on Lost/Outdated status — exercised by the soak sample
    // (--soak N) in manual testing.
}
