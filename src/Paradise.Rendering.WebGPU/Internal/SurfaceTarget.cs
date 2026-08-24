using System;
using WgSurfaceGetCurrentTextureStatus = WebGpuSharp.SurfaceGetCurrentTextureStatus;
using WgTexture = WebGpuSharp.Texture;
using WgTextureView = WebGpuSharp.TextureView;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>A window's swapchain as a render target: borrow the current texture, draw, present.
///
/// <see cref="Readable"/> is null because a borrowed texture does not outlive its frame — after
/// the present it is the compositor's again — so there is nothing for a caller to copy once
/// rendering has finished. Separately, the chain is configured <c>RenderAttachment</c> only (see
/// <see cref="SurfaceState"/>), so it is not copyable mid-frame either; that flag is ours to
/// change if a windowed capture is ever wanted, the lifetime is not.</summary>
internal sealed class SurfaceTarget(SurfaceState surface) : IPresentationTarget
{
    public TextureFormat ColorFormat => FormatConversions.FromWgpu(surface.Format);

    public uint Width => surface.Width;

    public uint Height => surface.Height;

    public WgTexture? Readable => null;

    /// <summary>The texture acquired for this frame, dropped again at <see cref="Present"/> so a
    /// stale one can never be handed out — the surface will have rotated it back into the chain.</summary>
    public WgTexture? CurrentTexture { get; private set; }

    public bool SupportsCapture => surface.CanCopyFrom;

    public void Resize(uint width, uint height) => surface.Resize(width, height);

    public bool TryAcquireView(out WgTextureView view)
    {
        var current = surface.Native.GetCurrentTexture();
        switch (current.Status)
        {
            case WgSurfaceGetCurrentTextureStatus.SuccessOptimal:
            case WgSurfaceGetCurrentTextureStatus.SuccessSuboptimal:
                break;
            case WgSurfaceGetCurrentTextureStatus.Outdated:
            case WgSurfaceGetCurrentTextureStatus.Lost:
                // Rebuild and skip: the swapchain itself is stale, so there is no texture to draw
                // into this frame. The next one gets a fresh chain.
                surface.Reconfigure();
                view = null!;
                return false;
            default:
                throw new InvalidOperationException($"Surface texture acquisition failed: {current.Status}");
        }

        var texture = current.Texture
            ?? throw new InvalidOperationException(
                $"Surface texture was null despite status {current.Status} — WebGPUSharp invariant violation.");
        CurrentTexture = texture;
        view = texture.CreateView();
        return true;
    }

    public void Present()
    {
        surface.Native.Present();
        // The texture belongs to the compositor again from here.
        CurrentTexture = null;
    }

    public void Dispose() => surface.Dispose();
}
