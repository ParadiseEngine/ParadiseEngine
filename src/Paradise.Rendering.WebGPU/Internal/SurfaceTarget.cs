using System;
using WgSurfaceGetCurrentTextureStatus = WebGpuSharp.SurfaceGetCurrentTextureStatus;
using WgTexture = WebGpuSharp.Texture;
using WgTextureView = WebGpuSharp.TextureView;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>A window's swapchain as a render target: borrow the current texture, draw, present.
/// The texture is the presentation engine's and is configured <c>RenderAttachment</c> only (see
/// <see cref="SurfaceState"/>), so <see cref="Readable"/> is null — there is nothing to copy
/// out of.</summary>
internal sealed class SurfaceTarget(SurfaceState surface) : IPresentationTarget
{
    public TextureFormat ColorFormat => FormatConversions.FromWgpu(surface.Format);

    public uint Width => surface.Width;

    public uint Height => surface.Height;

    public WgTexture? Readable => null;

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
        view = texture.CreateView();
        return true;
    }

    public void Present() => surface.Native.Present();

    public void Dispose() => surface.Dispose();
}
