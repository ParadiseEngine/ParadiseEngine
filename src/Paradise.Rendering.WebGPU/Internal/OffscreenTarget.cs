using System;
using WgExtent3D = WebGpuSharp.Extent3D;
using WgTexture = WebGpuSharp.Texture;
using WgTextureDescriptor = WebGpuSharp.TextureDescriptor;
using WgTextureDimension = WebGpuSharp.TextureDimension;
using WgTextureFormat = WebGpuSharp.TextureFormat;
using WgTextureUsage = WebGpuSharp.TextureUsage;
using WgTextureView = WebGpuSharp.TextureView;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>
/// A texture the renderer owns, for a run with no display: the same one is lent every frame, and
/// there is nobody to present it to.
///
/// It is created <c>CopySrc</c> as well as <c>RenderAttachment</c> — the flag that makes
/// <c>CopyTextureToBuffer</c> legal. Together with the fact that this texture is OURS and outlives
/// any single frame, that is what lets a caller read the finished image at its leisure rather than
/// having to catch it before a present.
/// </summary>
internal sealed class OffscreenTarget : IPresentationTarget
{
    private readonly WebGpuDevice _device;
    private WgTexture _texture;
    private bool _disposed;

    public OffscreenTarget(WebGpuDevice device, uint width, uint height)
    {
        _device = device;
        Width = width == 0 ? 1 : width;
        Height = height == 0 ? 1 : height;
        _texture = Create(device, Width, Height);
    }

    public TextureFormat ColorFormat => TextureFormat.Bgra8Unorm;

    public uint Width { get; private set; }

    public uint Height { get; private set; }

    public WgTexture? Readable => _texture;

    /// <summary>The same texture every frame — it is ours, so "current" and "readable" coincide.</summary>
    public WgTexture? CurrentTexture => _texture;

    /// <summary>Always: <see cref="Create"/> asks for <c>CopySrc</c> unconditionally.</summary>
    public bool SupportsCapture => true;

    public void Resize(uint width, uint height)
    {
        if (width == 0) width = 1;
        if (height == 0) height = 1;
        if (width == Width && height == Height)
        {
            return;
        }

        _texture.Destroy();
        Width = width;
        Height = height;
        _texture = Create(_device, width, height);
    }

    /// <summary>Always succeeds: the texture is ours and cannot go stale under us, which is the
    /// other half of why a headless run is reproducible where a swapchain one is not.</summary>
    public bool TryAcquireView(out WgTextureView view)
    {
        view = _texture.CreateView();
        return true;
    }

    /// <summary>Nothing to present to. The frame is finished and simply stays in the texture,
    /// which is where <c>ReadbackColor</c> finds it.</summary>
    public void Present()
    {
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _texture.Destroy();
    }

    private static WgTexture Create(WebGpuDevice device, uint width, uint height)
    {
        var desc = new WgTextureDescriptor
        {
            Label = "ParadiseHeadlessTarget",
            Size = new WgExtent3D(width, height, 1),
            MipLevelCount = 1,
            SampleCount = 1,
            Dimension = WgTextureDimension.D2,
            Format = WgTextureFormat.BGRA8Unorm,
            // CopySrc is the whole point: without it the finished frame cannot be copied out.
            Usage = WgTextureUsage.RenderAttachment | WgTextureUsage.CopySrc,
        };
        return device.Device.CreateTexture(in desc);
    }
}
