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

    [Test]
    public async Task surface_ctor_rejects_headless_platform()
    {
        var desc = SurfaceDescriptor.Headless();
        await Assert.That(() => new WebGpuRenderer(in desc)).Throws<ArgumentException>();
    }
}
