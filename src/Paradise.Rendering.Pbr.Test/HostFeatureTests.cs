using System.Numerics;
using Paradise.Rendering.Graph;
using Paradise.Rendering.Pbr.Test.Baseline;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

/// <summary>The seam from a host's side: a feature the renderer did not construct declares a
/// pass by name against the engine's targets and lands in the submitted frame.</summary>
public class HostFeatureTests
{
    private sealed class VignetteFeature : IRenderFeature
    {
        public int Recorded;
        public bool SawHdr;

        public string Name => "Vignette";
        public bool Enabled => true;
        public FrameRequirements Requires => FrameRequirements.None;
        public void Resize(uint width, uint height) { }

        public void Setup(in FrameContext frame)
        {
            SawHdr = frame.Textures.Contains(PbrTargets.Hdr);
            frame.Graph.AddRasterPass("Host.Vignette", RenderPassEvent.Overlay)
                .Color(0, FrameGraph.Backbuffer, LoadOp.Load)
                .Reads(frame.Graph.Texture(PbrTargets.Hdr))
                .Record(this, Record);
        }

        private static void Record(VignetteFeature self, ref PassRecording pass, int _) => self.Recorded++;

        public void Dispose() { }
    }

    [Test]
    public async Task a_host_feature_appends_its_pass_at_its_event()
    {
        WebGpuRenderer backend;
        try
        {
            backend = WebGpuRenderer.CreateHeadless(32, 32);
        }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No WebGPU adapter available on this host: {error.Message}");
            return;
        }
        using var _ = backend;
        var recorder = new RecordingRenderer(backend);
        using var pbr = new PbrRenderer(recorder, 32, 32);
        var vignette = new VignetteFeature();
        pbr.Pipeline.Add(vignette);
        var eye = new Vector3(0f, 1f, 3f);
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                View = PbrMath.LookAt(eye, Vector3.Zero, Vector3.UnitY),
                Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f),
                Position = eye,
            },
        };

        pbr.RenderFrame(scene);
        var frame = recorder.Frames[^1];
        var last = frame.Passes[^1];
        var passCount = frame.Passes.Length;

        await Assert.That(vignette.SawHdr).IsTrue();
        await Assert.That(vignette.Recorded).IsEqualTo(1);
        // Main, then the composite to the backbuffer, then the host's pass loading it.
        await Assert.That(passCount).IsEqualTo(3);
        await Assert.That(last[0].Load).IsEqualTo(LoadOp.Load);
        await Assert.That(last[0].ColorView.IsValid).IsFalse();
    }
}
