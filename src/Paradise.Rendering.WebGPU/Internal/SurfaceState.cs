using System;
using WgSurface = WebGpuSharp.Surface;
using WgTextureFormat = WebGpuSharp.TextureFormat;
using WgTextureUsage = WebGpuSharp.TextureUsage;
using WgSurfaceConfiguration = WebGpuSharp.SurfaceConfiguration;
using WgCompositeAlphaMode = WebGpuSharp.CompositeAlphaMode;
using WgPresentMode = WebGpuSharp.PresentMode;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Owns a configured WebGPU <see cref="WgSurface"/>: chosen swapchain format, current
/// width/height, and reconfiguration on resize.</summary>
internal sealed class SurfaceState : IDisposable
{
    private readonly WebGpuDevice _device;
    private readonly WgSurface _surface;
    private bool _disposed;

    public WgTextureFormat Format { get; private set; }
    public uint Width { get; private set; }
    public uint Height { get; private set; }

    public WgSurface Native => _surface;

    public SurfaceState(WebGpuDevice device, WgSurface surface, uint width, uint height)
    {
        _device = device;
        _surface = surface;
        Width = width == 0 ? 1 : width;
        Height = height == 0 ? 1 : height;

        var caps = surface.GetCapabilities(device.Adapter)
            ?? throw new InvalidOperationException("Surface.GetCapabilities returned null for the chosen adapter.");
        var formats = caps.Formats;
        Format = formats.Length > 0 ? formats[0] : WgTextureFormat.BGRA8Unorm;

        Configure();
    }

    public void Resize(uint width, uint height)
    {
        if (width == 0) width = 1;
        if (height == 0) height = 1;
        if (width == Width && height == Height) return;
        Width = width;
        Height = height;
        Configure();
    }

    private void Configure()
    {
        var config = new WgSurfaceConfiguration
        {
            Device = _device.Device,
            Format = Format,
            Usage = WgTextureUsage.RenderAttachment,
            AlphaMode = WgCompositeAlphaMode.Auto,
            PresentMode = WgPresentMode.Fifo,
            Width = Width,
            Height = Height,
        };
        _surface.Configure(in config);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _surface.Unconfigure();
    }
}
