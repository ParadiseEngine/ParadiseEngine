using System.Numerics;
using Paradise.Features;
using Paradise.Rendering.Graph;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

public class FrameLightingDependencyTests
{
    private const uint Size = 64;

    private sealed class FrameObserver : IRenderFeature
    {
        public FeatureDefinition Definition { get; } = new("test.frameLightingObserver", true);
        public FrameRequirements Requires => FrameRequirements.None;
        public bool HasLighting { get; private set; }
        public bool HasProbes { get; private set; }
        public bool HasIrradiance { get; private set; }
        public bool HasVisibility { get; private set; }
        public ProbeFrameData Probes { get; private set; }

        public void Setup(in FrameContext frame)
        {
            HasLighting = frame.Blackboard.TryGet(FrameLightingData.Key, out _);
            HasProbes = frame.Blackboard.TryGet(ProbeFrameData.Key, out var probes);
            HasIrradiance = frame.Blackboard.TryGet(PbrResults.GiIrradiance, out _);
            HasVisibility = frame.Blackboard.TryGet(PbrResults.GiVisibility, out _);
            Probes = probes;
        }

        public void Resize(uint width, uint height) { }
        public void Dispose() { }
    }

    private static WebGpuRenderer? Backend()
    {
        try { return WebGpuRenderer.CreateHeadless(Size, Size); }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No WebGPU adapter: {error.Message}");
            return null;
        }
    }

    private static PbrScene Configure(PbrRenderer pbr)
    {
        pbr.Switches.Set(PbrFeatures.GiProbes.Id, true);
        pbr.Pipeline.Find<ProbeGiDebugFeature>()!.ProbeRadius = 0.25f;
        pbr.Pipeline.Find<ProbeGiFeature>()!.Settings = new PbrGi
        {
            Enabled = true, RaysPerProbe = 8, ProbesPerFrame = 8,
            Volume = new PbrProbeVolume(new Vector3(-1), new Vector3(2), 2, 2, 2),
        };
        var eye = new Vector3(0, 0, 5);
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                Position = eye,
                View = PbrMath.LookAt(eye, Vector3.Zero, Vector3.UnitY),
                Projection = PbrMath.Orthographic(4, 1, 0.1f, 20),
            },
            ClearColor = new ColorRgba(0.1f, 0.2f, 0.3f, 1),
            Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Linear },
            Fog = new PbrFog
            {
                Enabled = true, Density = 0.2f, HeightFalloff = 0, MaxDistance = 4,
                Color = Vector3.Zero, LightScattering = true, Albedo = Vector3.One,
            },
        };
        scene.Lights.Add(new PbrLight { Type = PbrLightType.Directional, Direction = Vector3.UnitZ, Intensity = 1 });
        return scene;
    }

    [Test]
    public async Task missing_frame_lighting_retracts_gi_debug_and_fog_and_reenable_restores_the_picture()
    {
        using var backend = Backend();
        if (backend is null) return;
        var switches = new FeatureSwitches();
        switches.Set(PbrFeatures.FrameLighting.Id, false);
        using var pbr = new PbrRenderer(backend, switches, Size, Size);
        var observer = new FrameObserver();
        pbr.Pipeline.Add(observer);
        var scene = Configure(pbr);
        var gi = pbr.Pipeline.Find<ProbeGiFeature>()!;

        pbr.RenderFrame(scene);
        var initiallyDisabled = Pixels(backend);
        await AssertAbsent(pbr, observer, gi).ConfigureAwait(false);

        switches.Set(PbrFeatures.FrameLighting.Id, true);
        for (var i = 0; i < 3; i++) pbr.RenderFrame(scene);
        var active = Pixels(backend);
        await AssertActive(pbr, observer).ConfigureAwait(false);
        await Assert.That(active.SequenceEqual(initiallyDisabled)).IsFalse();

        switches.Set(PbrFeatures.FrameLighting.Id, false);
        pbr.RenderFrame(scene);
        await AssertAbsent(pbr, observer, gi).ConfigureAwait(false);
        // A populated HDR target from the previous frame must not keep displaying old probes.
        await Assert.That(Pixels(backend).SequenceEqual(initiallyDisabled)).IsTrue();

        switches.Set(PbrFeatures.FrameLighting.Id, true);
        pbr.RenderFrame(scene);
        await AssertActive(pbr, observer).ConfigureAwait(false);
        await Assert.That(gi.ProbeCount).IsEqualTo(8);
        await Assert.That(Pixels(backend).SequenceEqual(active)).IsTrue();
    }

    [Test]
    public async Task published_probe_state_matches_the_atlas_input_not_the_next_frame_update()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var observer = new FrameObserver();
        pbr.Pipeline.Add(observer);
        var scene = Configure(pbr);
        scene.Fog = scene.Fog with { Enabled = false };

        pbr.RenderFrame(scene);
        var first = observer.Probes;
        var awaiting = Pixels(backend);
        await Assert.That(observer.HasProbes && observer.HasIrradiance && observer.HasVisibility).IsTrue();
        await Assert.That(first.ProbeCount).IsEqualTo(8);
        var firstStates = backend.ReadbackBuffer(first.ShadingStateBuffer, 0, (ulong)first.ProbeCount * 16);
        for (var i = 0; i < first.ProbeCount; i++)
            await Assert.That(BitConverter.ToSingle(firstStates, i * 16 + 12)).IsEqualTo(-1f);

        pbr.RenderFrame(scene);
        var second = observer.Probes;
        await Assert.That(observer.HasProbes && observer.HasIrradiance && observer.HasVisibility).IsTrue();
        await Assert.That(second.ProbeCount).IsEqualTo(first.ProbeCount);
        await Assert.That(second.ShadingStateBuffer == first.ShadingStateBuffer).IsFalse();
        var secondStates = backend.ReadbackBuffer(second.ShadingStateBuffer, 0, (ulong)second.ProbeCount * 16);
        for (var i = 0; i < second.ProbeCount; i++)
            await Assert.That(BitConverter.ToSingle(secondStates, i * 16 + 12)).IsEqualTo(1f);
        // Empty space activates all probes on their first trace, changing debug markers red to green.
        await Assert.That(Pixels(backend).SequenceEqual(awaiting)).IsFalse();
    }

    private static async Task AssertAbsent(PbrRenderer pbr, FrameObserver observer, ProbeGiFeature gi)
    {
        await Assert.That(observer.HasLighting || observer.HasProbes || observer.HasIrradiance || observer.HasVisibility).IsFalse();
        await Assert.That(gi.ProbeCount).IsEqualTo(0);
        await Assert.That(gi.ActiveVolume).IsNull();
        await Assert.That(pbr.LastPassNames.Any(name => name.StartsWith("Gi.", StringComparison.Ordinal))).IsFalse();
        await Assert.That(pbr.LastPassNames.Contains("Fog.Integrate")).IsFalse();
    }

    private static async Task AssertActive(PbrRenderer pbr, FrameObserver observer)
    {
        await Assert.That(observer.HasLighting && observer.HasProbes && observer.HasIrradiance && observer.HasVisibility).IsTrue();
        await Assert.That(pbr.LastPassNames.Contains("Gi.Trace")).IsTrue();
        await Assert.That(pbr.LastPassNames.Contains("Gi.DebugProbes")).IsTrue();
        await Assert.That(pbr.LastPassNames.Contains("Fog.Integrate")).IsTrue();
    }

    private static byte[] Pixels(WebGpuRenderer backend) => backend.ReadbackColor(out _, out _).ToArray();
}
