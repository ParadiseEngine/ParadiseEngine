using System;
using WebGpuSharp.Marshalling;
using WgSurfaceTextureFFI = WebGpuSharp.FFI.SurfaceTextureFFI;
using WgTextureHandle = WebGpuSharp.FFI.TextureHandle;
using WgTextureViewHandle = WebGpuSharp.FFI.TextureViewHandle;
using WgSurfaceGetCurrentTextureStatus = WebGpuSharp.SurfaceGetCurrentTextureStatus;
using WgTexture = WebGpuSharp.Texture;
using WgTextureView = WebGpuSharp.TextureView;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Borrows a window's current swapchain texture until presentation.</summary>
/// <remarks>Readable is null because the texture does not outlive its frame; mid-frame capture
/// requires a copyable surface configuration. The texture and its view are raw handles released at
/// <see cref="Present"/>: WebGPUSharp's wrappers are freed only by finalizers, which a frame that
/// allocates nothing else leaves waiting for a collection that may not come for many minutes.
/// Wrappers are made only for what asks for one, a capture or a host pass.</remarks>
internal sealed class SurfaceTarget(SurfaceState surface) : IPresentationTarget
{
    private WgTextureHandle _texture;
    private WgTextureViewHandle _view;
    private WgTexture? _textureWrapper;
    private WgTextureView? _viewWrapper;

    public TextureFormat ColorFormat => FormatConversions.FromWgpu(surface.Format);

    public uint Width => surface.Width;

    public uint Height => surface.Height;

    public WgTexture? Readable => null;

    /// <summary>The texture acquired for this frame, dropped again at <see cref="Present"/> so a
    /// stale one can never be handed out — the surface will have rotated it back into the chain.</summary>
    /// <remarks>Its wrapper takes its own reference, released by its finalizer.</remarks>
    public WgTexture? CurrentTexture =>
        WgTextureHandle.IsNull(_texture) ? null : _textureWrapper ??= _texture.ToSafeHandle();

    public WgTextureView CurrentView =>
        WgTextureViewHandle.IsNull(_view)
            ? throw new InvalidOperationException("No surface texture is acquired.")
            : _viewWrapper ??= _view.ToSafeHandle()!;

    public bool SupportsCapture => surface.CanCopyFrom;

    /// <summary>Always: a full swapchain blocks acquisition until the compositor returns a texture.</summary>
    public bool PacesFrames => true;

    public void Resize(uint width, uint height) => surface.Resize(width, height);

    public bool TryAcquireView(out WgTextureViewHandle view)
    {
        // A frame whose submission threw never reached Present.
        ReleaseFrame();
        var current = new WgSurfaceTextureFFI();
        WebGPUMarshal.GetHandle(surface.Native).GetCurrentTexture(ref current);
        switch (current.Status)
        {
            case WgSurfaceGetCurrentTextureStatus.SuccessOptimal:
            case WgSurfaceGetCurrentTextureStatus.SuccessSuboptimal:
                break;
            case WgSurfaceGetCurrentTextureStatus.Outdated:
            case WgSurfaceGetCurrentTextureStatus.Lost:
                // Rebuild and skip: the swapchain itself is stale, so there is no texture to draw
                // into this frame. The next one gets a fresh chain.
                current.Texture.Dispose();
                surface.Reconfigure();
                view = default;
                return false;
            default:
                current.Texture.Dispose();
                throw new InvalidOperationException($"Surface texture acquisition failed: {current.Status}");
        }

        if (WgTextureHandle.IsNull(current.Texture))
            throw new InvalidOperationException(
                $"Surface texture was null despite status {current.Status} — WebGPU invariant violation.");
        // The caller owns a reference to the acquired texture and to the view it creates.
        _texture = current.Texture;
        _view = _texture.CreateView();
        view = _view;
        return true;
    }

    public void Present()
    {
        surface.Native.Present();
        // The texture belongs to the compositor again from here.
        ReleaseFrame();
    }

    public void Dispose()
    {
        ReleaseFrame();
        surface.Dispose();
    }

    private void ReleaseFrame()
    {
        _view.Dispose();
        _texture.Dispose();
        _view = default;
        _texture = default;
        _textureWrapper = null;
        _viewWrapper = null;
    }
}
