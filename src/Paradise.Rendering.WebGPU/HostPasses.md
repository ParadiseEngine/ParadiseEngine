# Native UI in the frame graph

Declare native ImGui or Noesis work with `FrameGraph.AddHostPass` at
`RenderPassEvent.Overlay`. `WebGpuRenderer.OverlayPass` has been removed.

```csharp
// Construct once on the render thread; keep the UI renderer alive through submission.
var callback = new WebGpuHostPass(noesis.RecordOverlay);

// In IRenderFeature.Setup, declare this each frame:
frame.Graph.AddHostPass("Noesis", RenderPassEvent.Overlay, callback, FrameGraph.Backbuffer);
```

For a host that only clears and draws UI, `ClearFrame.Graph` holds persistent declarations:

```csharp
var scene = new ClearFrame(background);
scene.Graph.AddHostPass("ImGui", RenderPassEvent.Overlay,
    new WebGpuHostPass((encoder, target) =>
    {
        overlay.ApplyTextureOps(pending);
        if (snapshot is not null)
            overlay.Render(encoder, target, width, height, snapshot);
    }), FrameGraph.Backbuffer);

// Update snapshot and pending operations before each submission.
renderer.Submit(scene.Record());
```

The callback executes during submission, outside any open stream pass, after earlier
passes and before later passes and frame capture. It must close all native passes it
opens. The target is modeled as loaded and stored; declare additional native texture
and buffer dependencies through `Reads` and `Writes`. A private target with no consumer
is culled, including its callback. `SubmitOffscreen` supports explicit imported targets.

Callbacks and their captured state remain caller-owned until submission completes.
The stream borrows commands, attachments and callbacks until its command writer is reset.
Compiling the graph into a different writer leaves earlier streams intact.
Keep per-frame state valid until submit; do not allocate a callback every frame.

The browser accepts and skips the HostPass opcode. Native UI needs a separate browser
implementation. Native host passes occupy a profiling slot but report zero duration,
since they own their internal native passes and timestamp recording.
