using System;
using WgInstance = WebGpuSharp.Instance;
using WgAdapter = WebGpuSharp.Adapter;
using WgDevice = WebGpuSharp.Device;
using WgQueue = WebGpuSharp.Queue;
using WgSurface = WebGpuSharp.Surface;
using WgRequestAdapterOptions = WebGpuSharp.RequestAdapterOptions;
using WgDeviceDescriptor = WebGpuSharp.DeviceDescriptor;
using WgPowerPreference = WebGpuSharp.PowerPreference;
using WgFeatureLevel = WebGpuSharp.FeatureLevel;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Owns the long-lived WebGPU instance/adapter/device/queue chain. Constructed once per
/// <see cref="WebGpuRenderer"/>. Adapter selection takes an optional compatible <see cref="WgSurface"/>
/// — pass <c>null</c> to drive the headless adapter path (no swapchain).</summary>
internal sealed class WebGpuDevice : IDisposable
{
    public WgInstance Instance { get; }
    public WgAdapter Adapter { get; }
    public WgDevice Device { get; }
    public WgQueue Queue { get; }

    private bool _disposed;

    private WebGpuDevice(WgInstance instance, WgAdapter adapter, WgDevice device, WgQueue queue)
    {
        Instance = instance;
        Adapter = adapter;
        Device = device;
        Queue = queue;
    }

    public static WebGpuDevice Create(WgInstance instance, WgSurface? compatibleSurface)
    {
        const ulong AdapterTimeoutNs = 10_000_000_000UL; // 10s — generous for cold first-run on CI

        // CompatibleSurface is a required init member on RequestAdapterOptions, so two literals
        // is simpler than reflection — picking the headless path means leaving it default-null.
        WgAdapter? adapter;
        if (compatibleSurface is not null)
        {
            var opts = new WgRequestAdapterOptions
            {
                CompatibleSurface = compatibleSurface,
                PowerPreference = WgPowerPreference.HighPerformance,
                FeatureLevel = WgFeatureLevel.Core,
            };
            adapter = instance.RequestAdapterSync(in opts, AdapterTimeoutNs);
        }
        else
        {
            var opts = new WgRequestAdapterOptions
            {
                CompatibleSurface = null!,
                PowerPreference = WgPowerPreference.HighPerformance,
                FeatureLevel = WgFeatureLevel.Core,
            };
            adapter = instance.RequestAdapterSync(in opts, AdapterTimeoutNs);
        }

        if (adapter is null)
            throw new AdapterUnavailableException(
                "No WebGPU adapter available. On Linux without a GPU, install mesa-vulkan-drivers / libvulkan1 (lavapipe) for headless support.");

        var deviceDesc = new WgDeviceDescriptor
        {
            Label = "Paradise.Rendering.WebGPU",
            UncapturedErrorCallback = static (type, message) =>
            {
                var text = message.Length == 0 ? "(no message)" : System.Text.Encoding.UTF8.GetString(message);
                Console.Error.WriteLine($"[WebGPU] {type}: {text}");
            },
        };

        var device = adapter.RequestDeviceSync(in deviceDesc, AdapterTimeoutNs)
            ?? throw new InvalidOperationException("WebGPU device creation failed.");

        var queue = device.GetQueue();
        return new WebGpuDevice(instance, adapter, device, queue);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // WebGPUSharp's safe wrappers release native handles via finalizers; explicit teardown
        // ordering here is mostly documentation. The instance must outlive surfaces and devices,
        // so the owning Renderer disposes those first.
    }
}
