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

    /// <summary>Whether the swapchain's textures were configured <c>CopySrc</c>, and can therefore
    /// be copied out of mid-frame. False when capture was not asked for, and also when it was but
    /// the surface does not advertise the usage.</summary>
    public bool CanCopyFrom { get; private set; }

    public WgTextureFormat Format { get; private set; }
    public uint Width { get; private set; }
    public uint Height { get; private set; }

    public WgSurface Native => _surface;

    /// <param name="allowCapture">Ask for <c>CopySrc</c> on the chain's textures, so a frame can be
    /// copied before it is presented. OFF by default and deliberately so: a backbuffer that must be
    /// copyable can cost the driver optimisations it would otherwise apply to every frame, and that
    /// is not a price to charge a host that never captures.</param>
    public SurfaceState(WebGpuDevice device, WgSurface surface, uint width, uint height,
        bool allowCapture = false)
    {
        _device = device;
        _surface = surface;
        Width = width == 0 ? 1 : width;
        Height = height == 0 ? 1 : height;

        var caps = surface.GetCapabilities(device.Adapter)
            ?? throw new InvalidOperationException("Surface.GetCapabilities returned null for the chosen adapter.");
        var formats = caps.Formats;
        Format = formats.Length > 0 ? formats[0] : WgTextureFormat.BGRA8Unorm;
        // Asked for only if the surface says it can — requesting an unsupported usage fails
        // configuration outright, which would turn "capture unavailable" into "no window".
        CanCopyFrom = allowCapture && (caps.Usages & WgTextureUsage.CopySrc) != 0;

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

    /// <summary>Re-apply the current configuration without changing dimensions. Used by the
    /// renderer when <c>GetCurrentTexture</c> reports <c>Outdated</c> / <c>Lost</c> — the
    /// swapchain itself needs to be rebuilt even though the requested size hasn't changed.</summary>
    public void Reconfigure() => Configure();

    private void Configure()
    {
        var config = new WgSurfaceConfiguration
        {
            Device = _device.Device,
            Format = Format,
            // RenderAttachment only. The chain's textures are therefore not copyable — which is a
            // CHOICE, not a platform rule: GetCapabilities above reports the usages this surface
            // supports and we read only its formats. Adding CopySrc (guarded by that capability)
            // is what a windowed screenshot would need, and it would have to be recorded mid-frame,
            // before the present that ends the texture's life.
            Usage = CanCopyFrom
                ? WgTextureUsage.RenderAttachment | WgTextureUsage.CopySrc
                : WgTextureUsage.RenderAttachment,
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
