namespace Paradise.Ui.Noesis;

/// <summary>The WebGPU render half of a NoesisGUI overlay: lazily initializes a
/// <see cref="NoesisRenderDevice"/> against the shared <see cref="NoesisViewCore"/> once the
/// sim thread has published the view, then records the UI passes (offscreen surfaces + the
/// onscreen composite, LoadOp.Load) into the host's frame encoder — the shape an engine
/// <c>OverlayPass</c> callback expects. Takes the raw WebGPU device and color format so this
/// package stays free of Paradise.Rendering dependencies; hosts on
/// <c>Paradise.Rendering.WebGPU</c> pass <c>renderer.NativeDevice</c> and the swapchain
/// format. Render-thread only, matching Noesis's threading contract
/// (<c>Renderer.Init</c>/<c>Render*</c> on the render thread, the View on its sim
/// thread).</summary>
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

        // Deliberately ignores the changed-flag and re-records every frame. The backbuffer is a
        // fresh swapchain texture each time and the scene passes have just painted over it, so
        // "nothing changed in the UI" does NOT mean the last UI image is still there — skipping
        // would present a frame with no UI on it. The flag is for hosts drawing into a target
        // that persists; see NoesisViewCore.TryUpdateRenderTree(out bool).
        if (!core.TryUpdateRenderTree()) return;
        _device.BeginFrame(encoder, backbuffer, core.Width, core.Height);
        view.Renderer.RenderOffscreen();
        view.Renderer.Render();
        _device.EndFrame();
    }
}
