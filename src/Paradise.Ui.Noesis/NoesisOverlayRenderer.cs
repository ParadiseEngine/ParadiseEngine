namespace Paradise.Ui.Noesis;

/// <summary>Records Noesis offscreen and overlay passes on the render thread.</summary>
/// <remarks>Initialization waits for the simulation thread to publish the view. Hosts supply the
/// native WebGPU device and format; LoadOp.Load composites the UI over the scene.</remarks>
public sealed class NoesisOverlayRenderer(
    NoesisViewCore core,
    WebGpuSharp.Device device,
    WebGpuSharp.TextureFormat colorFormat)
{
    private NoesisRenderDevice? _device;

    /// <summary>The render device, once the first frame after view publication has created
    /// it; null until then. Exposed for diagnostics (e.g. <see cref="NoesisRenderDevice.Unsupported"/>).</summary>
    public NoesisRenderDevice? Device => _device;

    /// <summary>Record the UI passes into the frame (render thread). No-op while the sim
    /// thread has not created the view yet.</summary>
    public void RecordOverlay(WebGpuSharp.CommandEncoder encoder, WebGpuSharp.TextureView backbuffer)
    {
        var view = core.View;
        if (view is null) return; // sim thread has not created the UI yet — skip this frame

        if (_device is null)
        {
            // Deliberately outside the core's sync lock: Noesis's threading contract runs
            // Renderer.Init on the render thread while the View lives on the UI thread — Init
            // touches only render-side state, so it may overlap a concurrent sim-thread
            // View.Update. Only UpdateRenderTree synchronizes the two trees.
            _device = new NoesisRenderDevice(device, colorFormat);
            view.Renderer.Init(_device);
            _device.PrewarmPipelines();
        }

        // Always redraw into a fresh swapchain target. The unchanged flag can skip rendering only
        // when the target preserves the previous UI image.
        if (!core.TryUpdateRenderTree()) return;
        _device.BeginFrame(encoder, backbuffer, core.Width, core.Height);
        view.Renderer.RenderOffscreen();
        view.Renderer.Render();
        _device.EndFrame();
    }
}
