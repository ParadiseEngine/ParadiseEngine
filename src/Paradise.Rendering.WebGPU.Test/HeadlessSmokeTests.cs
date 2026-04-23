using TUnit.Core;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>Smoke tests that exercise the headless adapter path. These hit live Dawn natives via
/// WebGPUSharp; if no Vulkan/Metal/DX12 backend is available (no GPU, no lavapipe on Linux CI)
/// the tests skip cleanly via <see cref="Skip.Test(string)"/> on <see cref="AdapterUnavailableException"/>
/// rather than failing. Device-creation or any other backend failure surfaces as a real test
/// failure — only adapter unavailability is treated as "not applicable on this host". The AOT
/// publish in CI is the load-bearing M0 acceptance signal; these are belt-and-suspenders.</summary>
public class HeadlessSmokeTests
{
    [Test]
    public async Task headless_renderer_initializes_and_disposes()
    {
        WebGpuRenderer? renderer;
        try
        {
            renderer = WebGpuRenderer.CreateHeadless(64, 64);
        }
        catch (AdapterUnavailableException ex)
        {
            Skip.Test($"No WebGPU adapter available on this host: {ex.Message}");
            return;
        }

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
        WebGpuRenderer? renderer;
        try
        {
            renderer = WebGpuRenderer.CreateHeadless(32, 32);
        }
        catch (AdapterUnavailableException ex)
        {
            Skip.Test($"No WebGPU adapter available on this host: {ex.Message}");
            return;
        }

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
        WebGpuRenderer? renderer;
        try
        {
            renderer = WebGpuRenderer.CreateHeadless(16, 16);
        }
        catch (AdapterUnavailableException ex)
        {
            Skip.Test($"No WebGPU adapter available on this host: {ex.Message}");
            return;
        }

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
    public async Task surface_ctor_rejects_headless_platform()
    {
        var desc = SurfaceDescriptor.Headless();
        await Assert.That(() => new WebGpuRenderer(in desc)).Throws<ArgumentException>();
    }
}
