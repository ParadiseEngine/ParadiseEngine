using Paradise.Rendering.WebGPU.Internal;
using Paradise.Windowing;
using TUnit.Core;

namespace Paradise.Rendering.WebGPU.Test;

public class BackendSelectionTests
{
    [Test]
    [Arguments(WebGpuBackend.Default, WebGpuSharp.BackendType.Undefined)]
    [Arguments(WebGpuBackend.Vulkan, WebGpuSharp.BackendType.Vulkan)]
    [Arguments(WebGpuBackend.Direct3D12, WebGpuSharp.BackendType.D3D12)]
    [Arguments(WebGpuBackend.Metal, WebGpuSharp.BackendType.Metal)]
    public async Task backend_maps_to_the_requested_native_api(WebGpuBackend backend, WebGpuSharp.BackendType expected)
    {
        await Assert.That(WebGpuDevice.NativeBackend(backend)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(999)]
    public async Task constructor_rejects_unknown_backends_before_loading_native_libraries(int value)
    {
        await Assert.That(() => new WebGpuRenderer(SurfaceDescriptor.Headless(1, 1), (WebGpuBackend)value))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(999)]
    public async Task headless_factory_rejects_unknown_backends_before_loading_native_libraries(int value)
    {
        await Assert.That(() => WebGpuRenderer.CreateHeadless((WebGpuBackend)value))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task explicit_vulkan_renderer_reports_vulkan_and_reads_back_a_clear_frame(bool useFactory)
    {
        WebGpuRenderer renderer;
        try
        {
            renderer = useFactory
                ? WebGpuRenderer.CreateHeadless(WebGpuBackend.Vulkan, 16, 16)
                : new WebGpuRenderer(SurfaceDescriptor.Headless(16, 16), WebGpuBackend.Vulkan);
        }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"Vulkan unavailable on this host: {error.Message}");
            return;
        }

        using (renderer)
        {
            var status = renderer.NativeDevice.GetAdapterInfo(out var adapter);
            await Assert.That(status).IsEqualTo(WebGpuSharp.Status.Success);
            await Assert.That(adapter.BackendType).IsEqualTo(WebGpuSharp.BackendType.Vulkan);

            renderer.RenderClearFrame(new ColorRgba(1, 0, 0, 1));
            var pixels = renderer.ReadbackColor(out var width, out var height);
            await Assert.That(width).IsEqualTo(16u);
            await Assert.That(height).IsEqualTo(16u);
            await Assert.That(pixels.Length).IsEqualTo(16 * 16 * 4);
            await Assert.That(pixels[0]).IsEqualTo((byte)0);
            await Assert.That(pixels[1]).IsEqualTo((byte)0);
            await Assert.That(pixels[2]).IsEqualTo((byte)255);
            await Assert.That(pixels[3]).IsEqualTo((byte)255);
        }
    }
}
