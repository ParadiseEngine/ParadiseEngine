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
    public async Task explicit_factory_rejects_unknown_backends_before_loading_native_libraries(int value)
    {
        await Assert.That(() => WebGpuRenderer.CreateWithBackend(SurfaceDescriptor.Headless(1, 1), (WebGpuBackend)value))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(999)]
    public async Task headless_factory_rejects_unknown_backends_before_loading_native_libraries(int value)
    {
        await Assert.That(() => WebGpuRenderer.CreateHeadlessWithBackend((WebGpuBackend)value))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task existing_zero_and_default_literal_calls_remain_unambiguous()
    {
        // Compilation is the regression check; these factories need not load Dawn.
        Func<WebGpuRenderer>[] factories =
        [
            () => WebGpuRenderer.CreateHeadless(0),
            () => WebGpuRenderer.CreateHeadless(0, 16),
            () => WebGpuRenderer.CreateHeadless(0, 16, default),
            () => new WebGpuRenderer(SurfaceDescriptor.Headless(1, 1), default),
            () => new WebGpuRenderer(SurfaceDescriptor.Headless(1, 1), default, default),
        ];
        await Assert.That(factories.Length).IsEqualTo(5);
    }

    [Test]
    public async Task unavailable_backend_preserves_the_native_error_without_falling_back()
    {
        var backend = OperatingSystem.IsMacOS() ? WebGpuBackend.Direct3D12 : WebGpuBackend.Metal;
        AdapterUnavailableException? failure = null;
        try
        {
            using var renderer = WebGpuRenderer.CreateHeadlessWithBackend(backend);
        }
        catch (AdapterUnavailableException exception)
        {
            failure = exception;
        }
        catch (DllNotFoundException exception)
        {
            Skip.Test($"WebGPU native library unavailable: {exception.Message}");
            return;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.InnerException is WebGpuSharp.WebGPUException).IsTrue();
        await Assert.That(failure.Message.Contains(WebGpuDevice.NativeBackend(backend).ToString(), StringComparison.Ordinal)).IsTrue();
        await Assert.That(failure.Message.Contains("No fallback was attempted.", StringComparison.Ordinal)).IsTrue();
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
                ? WebGpuRenderer.CreateHeadlessWithBackend(WebGpuBackend.Vulkan, 16, 16)
                : WebGpuRenderer.CreateWithBackend(SurfaceDescriptor.Headless(16, 16), WebGpuBackend.Vulkan);
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
