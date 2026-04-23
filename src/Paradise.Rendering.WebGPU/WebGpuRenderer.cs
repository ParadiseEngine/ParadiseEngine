using System;
using Paradise.Rendering.WebGPU.Internal;
using WgWebGPU = WebGpuSharp.WebGPU;
using WgTextureView = WebGpuSharp.TextureView;
using WgTexture = WebGpuSharp.Texture;
using WgTextureDescriptor = WebGpuSharp.TextureDescriptor;
using WgTextureFormat = WebGpuSharp.TextureFormat;
using WgTextureUsage = WebGpuSharp.TextureUsage;
using WgTextureDimension = WebGpuSharp.TextureDimension;
using WgRenderPassDescriptor = WebGpuSharp.RenderPassDescriptor;
using WgRenderPassColorAttachment = WebGpuSharp.RenderPassColorAttachment;
using WgLoadOp = WebGpuSharp.LoadOp;
using WgStoreOp = WebGpuSharp.StoreOp;
using WgColor = WebGpuSharp.Color;
using WgExtent3D = WebGpuSharp.Extent3D;
using WgSurfaceGetCurrentTextureStatus = WebGpuSharp.SurfaceGetCurrentTextureStatus;

namespace Paradise.Rendering.WebGPU;

/// <summary>WebGPU (Dawn) backend entry point. Constructed with a <see cref="SurfaceDescriptor"/>
/// for windowed rendering, or via <see cref="CreateHeadless"/> for the offscreen adapter path
/// used by CI smoke tests. M0 exposes only the clear-color present loop; the command-stream
/// surface (<c>BeginFrame</c>/<c>Submit</c>/<c>EndFrame</c>) lands in M1.</summary>
public sealed class WebGpuRenderer : IDisposable
{
    private readonly WebGpuDevice _device;
    private readonly SurfaceState? _surface;
    private readonly bool _isHeadless;
    private WgTexture? _offscreenTarget;
    private uint _offscreenWidth;
    private uint _offscreenHeight;
    private bool _disposed;

    /// <summary>Create a surface-backed renderer. The descriptor's platform tag selects the native
    /// surface variant; <see cref="SurfacePlatform.Headless"/> is rejected — use
    /// <see cref="CreateHeadless"/> instead.</summary>
    public WebGpuRenderer(in SurfaceDescriptor surface)
    {
        if (surface.Platform == SurfacePlatform.Headless)
            throw new ArgumentException(
                "Headless platform must use WebGpuRenderer.CreateHeadless(); the surface ctor requires a real native window.",
                nameof(surface));

        var instance = WgWebGPU.CreateInstance()
            ?? throw new InvalidOperationException("WebGPU.CreateInstance returned null — Dawn natives may be missing.");
        var nativeSurface = SurfaceFactory.Create(instance, surface);
        _device = WebGpuDevice.Create(instance, nativeSurface);
        _surface = new SurfaceState(_device, nativeSurface, surface.Width, surface.Height);
        _isHeadless = false;
    }

    private WebGpuRenderer(uint width, uint height)
    {
        var instance = WgWebGPU.CreateInstance()
            ?? throw new InvalidOperationException("WebGPU.CreateInstance returned null — Dawn natives may be missing.");
        _device = WebGpuDevice.Create(instance, compatibleSurface: null);
        _isHeadless = true;
        _offscreenWidth = width == 0 ? 1 : width;
        _offscreenHeight = height == 0 ? 1 : height;
        _offscreenTarget = CreateOffscreenTarget(_offscreenWidth, _offscreenHeight);
    }

    /// <summary>Construct the renderer using the headless adapter path. No native surface is
    /// created; clear frames render into an offscreen <c>BGRA8Unorm</c> texture sized
    /// <paramref name="width"/> x <paramref name="height"/>. The CI smoke test driver consumes
    /// this path with <c>SDL_VIDEODRIVER=dummy</c>.</summary>
    public static WebGpuRenderer CreateHeadless(uint width = 1, uint height = 1) => new(width, height);

    /// <summary>Resize the surface (or offscreen target) to <paramref name="width"/> x
    /// <paramref name="height"/>. Zero-sized requests are clamped to 1.</summary>
    public void Resize(uint width, uint height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isHeadless)
        {
            if (width == 0) width = 1;
            if (height == 0) height = 1;
            if (width == _offscreenWidth && height == _offscreenHeight) return;
            _offscreenTarget?.Destroy();
            _offscreenWidth = width;
            _offscreenHeight = height;
            _offscreenTarget = CreateOffscreenTarget(width, height);
        }
        else
        {
            _surface!.Resize(width, height);
        }
    }

    /// <summary>Acquire the next color attachment, run a single render pass that clears it to
    /// <paramref name="clearColor"/>, submit, and present (windowed) or simply discard (headless).</summary>
    public void RenderClearFrame(in ColorRgba clearColor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        WgTextureView view;
        if (_isHeadless)
        {
            view = _offscreenTarget!.CreateView();
        }
        else
        {
            var current = _surface!.Native.GetCurrentTexture();
            switch (current.Status)
            {
                case WgSurfaceGetCurrentTextureStatus.SuccessOptimal:
                case WgSurfaceGetCurrentTextureStatus.SuccessSuboptimal:
                    break;
                case WgSurfaceGetCurrentTextureStatus.Outdated:
                case WgSurfaceGetCurrentTextureStatus.Lost:
                    // Surface needs reconfigure (window resized between events, GPU lost, etc.).
                    // Skip this frame — caller will retry on the next tick. Force-reconfigure
                    // even if dimensions match: the swapchain itself must be rebuilt.
                    _surface.Reconfigure();
                    return;
                default:
                    throw new InvalidOperationException($"Surface texture acquisition failed: {current.Status}");
            }
            view = current.Texture!.CreateView();
        }

        var encoder = _device.Device.CreateCommandEncoder();

        var colors = new WgRenderPassColorAttachment[1];
        colors[0] = new WgRenderPassColorAttachment
        {
            View = view,
            LoadOp = WgLoadOp.Clear,
            StoreOp = WgStoreOp.Store,
            ClearValue = new WgColor(clearColor.R, clearColor.G, clearColor.B, clearColor.A),
            DepthSlice = null,
        };

        var passDesc = new WgRenderPassDescriptor
        {
            ColorAttachments = colors,
            Label = "ParadiseClearPass",
        };
        var pass = encoder.BeginRenderPass(in passDesc);
        pass.End();

        var commandBuffer = encoder.Finish();
        _device.Queue.Submit(commandBuffer);

        if (!_isHeadless)
        {
            _surface!.Native.Present();
        }
    }

    private WgTexture CreateOffscreenTarget(uint width, uint height)
    {
        var desc = new WgTextureDescriptor
        {
            Label = "ParadiseHeadlessTarget",
            Size = new WgExtent3D(width, height, 1),
            MipLevelCount = 1,
            SampleCount = 1,
            Dimension = WgTextureDimension.D2,
            Format = WgTextureFormat.BGRA8Unorm,
            Usage = WgTextureUsage.RenderAttachment | WgTextureUsage.CopySrc,
        };
        return _device.Device.CreateTexture(in desc);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _offscreenTarget?.Destroy();
        _surface?.Dispose();
        _device.Dispose();
    }
}
