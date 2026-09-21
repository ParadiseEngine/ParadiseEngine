using System.Numerics;
using Paradise.Features;
using Paradise.Rendering.Graph;
using Paradise.Rendering.Pbr.Test.Baseline;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

public class ProbeGiAtlasTests
{
    private const int AxisCount = 3;
    private const int ProbeCount = AxisCount * AxisCount * AxisCount;
    private const uint Size = 32;

    [Test]
    public async Task sparse_updates_preserve_unselected_atlas_tiles_across_ping_pong_and_budget_changes()
    {
        WebGpuRenderer backend;
        try
        {
            backend = WebGpuRenderer.CreateHeadless(Size, Size);
        }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No WebGPU adapter available on this host: {error.Message}");
            return;
        }
        using var lifetime = backend;
        var recorder = new RecordingRenderer(backend);
        using var pbr = new PbrRenderer(recorder, new FeatureSwitches(), Size, Size);
        var gi = pbr.Pipeline.Find<ProbeGiFeature>()!;
        gi.Settings = new PbrGi
        {
            Enabled = true, RaysPerProbe = 8, ProbesPerFrame = 2, Hysteresis = 0,
            Volume = new PbrProbeVolume(new Vector3(-1), Vector3.One, AxisCount, AxisCount, AxisCount),
        };
        var capture = new AtlasCapture(backend);
        pbr.Pipeline.Add(capture);
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                View = PbrMath.LookAt(new Vector3(0, 0, 5), Vector3.Zero, Vector3.UnitY),
                Projection = PbrMath.Perspective(MathF.PI / 3, 1, 0.1f, 100),
                Position = new Vector3(0, 0, 5),
            },
        };
        byte[] previous = new byte[AtlasCapture.Bytes];
        // Changing sky makes every selected tile change, without stochastic ray differences.
        // The two full updates also exercise the transition from full to sparse maintenance.
        for (var frame = 0; frame < 36; frame++)
        {
            var budget = frame is 15 or 16 ? ProbeCount : frame % 3 + 1;
            gi.Settings = gi.Settings with { ProbesPerFrame = budget };
            var sky = new Vector3((frame + 1) * 0.125f);
            scene.Ambient = new PbrAmbient { Sky = sky, Equator = sky, Ground = sky, Flat = true };
            pbr.RenderFrame(scene);
            var passIndex = -1;
            foreach (var command in recorder.Frames[^1].Commands)
            {
                if (command.Kind is RenderCommandKind.BeginPass or RenderCommandKind.BeginComputePass) passIndex++;
                if (command.Kind == RenderCommandKind.Dispatch && pbr.LastPassNames[passIndex] == "Gi.Blend")
                    await Assert.That(command.Dispatch.WorkgroupCountX).IsEqualTo((uint)budget);
            }
            var current = capture.Read();
            var changedIrradiance = 0;
            for (var probe = 0; probe < ProbeCount; probe++)
            {
                var irradianceChanged = !TileEquals(previous, current, probe, 10, 0);
                var visibilityChanged = !TileEquals(previous, current, probe, 16, ProbeCount * 100 * 16);
                if (irradianceChanged) changedIrradiance++;
                // Distance history can change only where this frame changed irradiance.
                await Assert.That(!visibilityChanged || irradianceChanged).IsTrue();
                if (!irradianceChanged) continue;
                var firstInterior = TileByteOffset(probe, 10, 1, 1, 0);
                var value = BitConverter.ToSingle(current, firstInterior);
                await Assert.That(value).IsEqualTo(sky.X).Within(0.01f);
            }
            await Assert.That(changedIrradiance).IsEqualTo(budget);
            previous = current;
        }
    }

    [Test]
    public async Task sparse_history_survives_scroll_disable_invalidation_and_reallocation()
    {
        WebGpuRenderer backend;
        try
        {
            backend = WebGpuRenderer.CreateHeadless(Size, Size);
        }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No WebGPU adapter available on this host: {error.Message}");
            return;
        }
        using var lifetime = backend;
        var switches = new FeatureSwitches();
        using var pbr = new PbrRenderer(backend, switches, Size, Size);
        var gi = pbr.Pipeline.Find<ProbeGiFeature>()!;
        var volume = new PbrProbeVolume(new Vector3(-1), Vector3.One, 3, 3, 3);
        gi.Settings = new PbrGi { Enabled = true, RaysPerProbe = 8, Hysteresis = 0, Scrolling = true, Volume = volume };
        var capture = new AtlasCapture(backend, 64);
        pbr.Pipeline.Add(capture);
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                View = PbrMath.LookAt(new Vector3(0, 0, 5), Vector3.Zero, Vector3.UnitY),
                Projection = PbrMath.Perspective(MathF.PI / 3, 1, 0.1f, 100),
                Position = new Vector3(0, 0, 5),
            },
        };
        var frameNumber = 0;
        byte[]? previous = null;
        async Task<int> RenderAndCheck()
        {
            var sky = new Vector3(++frameNumber * 0.125f);
            scene.Ambient = new PbrAmbient { Sky = sky, Equator = sky, Ground = sky, Flat = true };
            pbr.RenderFrame(scene);
            var current = capture.Read();
            var states = backend.ReadbackBuffer(gi.ShadingStateBuffer, 0, (ulong)gi.ProbeCount * 16);
            var active = 0;
            var resident = gi.ActiveVolume!;
            var rowTiles = resident.CountX * resident.CountY;
            for (var probe = 0; probe < gi.ProbeCount; probe++)
            {
                if (BitConverter.ToSingle(states, probe * 16 + 12) < 0.5f) continue;
                active++;
                var valueOffset = TileByteOffset(probe, 10, 1, 1, 0, rowTiles);
                var value = BitConverter.ToSingle(current, valueOffset);
                await Assert.That(value).IsGreaterThan(0f);
                // A retained active tile either keeps its exact history or is overwritten by
                // this frame's sky; a stale ping-pong side must never resurrect older lighting.
                if (previous is not null && !TileEquals(previous, current, probe, 10, 0, rowTiles))
                    await Assert.That(value).IsEqualTo(sky.X).Within(0.01f);
            }
            previous = current;
            return active;
        }

        await RenderAndCheck().ConfigureAwait(false);
        await Assert.That(await RenderAndCheck().ConfigureAwait(false)).IsEqualTo(27);
        gi.Settings = gi.Settings with { ProbesPerFrame = 2, UpdateFocus = Vector3.Zero };
        await RenderAndCheck().ConfigureAwait(false);
        switches.Set(PbrFeatures.GlobalIllumination.Id, false);
        for (var i = 0; i < 3; i++) pbr.RenderFrame(scene);
        await Assert.That(pbr.LastPassNames.Any(n => n.StartsWith("Gi.", StringComparison.Ordinal))).IsFalse();
        switches.Set(PbrFeatures.GlobalIllumination.Id, true);
        gi.Settings = gi.Settings with { ProbesPerFrame = 1 };
        await Assert.That(await RenderAndCheck().ConfigureAwait(false)).IsEqualTo(27);

        gi.Settings = gi.Settings with { Volume = volume with { Origin = volume.Origin + Vector3.UnitX } };
        await Assert.That(await RenderAndCheck().ConfigureAwait(false)).IsEqualTo(18);
        for (var i = 0; i < 9; i++) await RenderAndCheck().ConfigureAwait(false);
        await Assert.That(await RenderAndCheck().ConfigureAwait(false)).IsEqualTo(27);

        gi.Invalidate(new Geometry.Aabb(new Vector3(1), new Vector3(1)));
        await Assert.That(await RenderAndCheck().ConfigureAwait(false)).IsLessThan(27);
        for (var i = 0; i < 28; i++) await RenderAndCheck().ConfigureAwait(false);
        await Assert.That(await RenderAndCheck().ConfigureAwait(false)).IsEqualTo(27);

        // Grow both atlases and state buffers while a sparse carry is pending.
        gi.Settings = gi.Settings with
        {
            ProbesPerFrame = 7,
            Volume = new PbrProbeVolume(new Vector3(-2), Vector3.One, 4, 4, 4),
        };
        previous = null;
        await Assert.That(await RenderAndCheck().ConfigureAwait(false)).IsEqualTo(0);
        for (var i = 0; i < 10; i++) await RenderAndCheck().ConfigureAwait(false);
        await Assert.That(await RenderAndCheck().ConfigureAwait(false)).IsEqualTo(64);

        // A teleport keeps allocation sizes but invalidates every slot, including pending carry.
        gi.Settings = gi.Settings with { Volume = gi.Settings.Volume! with { Origin = new Vector3(100) } };
        await Assert.That(await RenderAndCheck().ConfigureAwait(false)).IsEqualTo(0);
        for (var i = 0; i < 10; i++) await RenderAndCheck().ConfigureAwait(false);
        await Assert.That(await RenderAndCheck().ConfigureAwait(false)).IsEqualTo(64);
    }

    private static bool TileEquals(byte[] first, byte[] second, int probe, int tile, int start, int rowTiles = AxisCount * AxisCount)
    {
        for (var y = 0; y < tile; y++)
        {
            var offset = TileByteOffset(probe, tile, 0, y, start, rowTiles);
            if (!first.AsSpan(offset, tile * 16).SequenceEqual(second.AsSpan(offset, tile * 16))) return false;
        }
        return true;
    }

    private static int TileByteOffset(int probe, int tile, int x, int y, int start, int rowTiles = AxisCount * AxisCount)
    {
        var tileX = probe % rowTiles;
        var tileY = probe / rowTiles;
        var width = rowTiles * tile;
        return start + ((tileY * tile + y) * width + tileX * tile + x) * 16;
    }

    private sealed class AtlasCapture : IRenderFeature
    {
        internal const int Bytes = ProbeCount * (100 + 256) * 16;
        private readonly WebGpuRenderer _backend;
        private readonly ShaderProgramDesc _program;
        private readonly ComputePipelineHandle _pipeline;
        private readonly BufferHandle _buffer;
        private readonly int _bytes;

        internal AtlasCapture(WebGpuRenderer backend, int capacity = ProbeCount)
        {
            _backend = backend;
            _bytes = capacity * (100 + 256) * 16;
            _program = ShaderProgramLoader.Load(typeof(ProbeGiAtlasTests).Assembly, "Shaders.probeAtlasReadback");
            _pipeline = backend.CreateComputePipeline(_program);
            _buffer = backend.CreateBuffer(new BufferDesc("ProbeAtlasReadback", (ulong)_bytes, BufferUsage.Storage | BufferUsage.CopySrc));
        }

        public FeatureDefinition Definition { get; } = new("test.probeAtlasCapture", true);
        public FrameRequirements Requires => FrameRequirements.None;
        public void Resize(uint width, uint height) { }
        internal byte[] Read() => _backend.ReadbackBuffer(_buffer, 0, (ulong)_bytes);

        public void Setup(in FrameContext frame)
        {
            if (!frame.Blackboard.TryGet(PbrResults.GiIrradiance, out var irradiance)
                || !frame.Blackboard.TryGet(PbrResults.GiVisibility, out var visibility)) return;
            frame.Graph.AddComputePass("Test.ProbeAtlasReadback", RenderPassEvent.BeforePrepass)
                .BindGroup(0, "ProbeAtlasReadbackGroup", ShaderPrograms.FindGroup(_program, 0),
                [
                    GraphBinding.Texture(0, irradiance),
                    GraphBinding.Texture(1, visibility),
                    GraphBinding.Buffer(2, _buffer, 0, (ulong)_bytes),
                ])
                .NeverCull()
                .Record(this, static (AtlasCapture self, ref PassRecording pass, int _) =>
                {
                    pass.Encoder.SetComputePipeline(self._pipeline);
                    pass.SetBindGroup(0);
                    pass.Encoder.Dispatch(new DispatchCommand((uint)((self._bytes / 16 + 63) / 64), 1, 1));
                }, 0);
        }

        public void Dispose()
        {
            _backend.DestroyComputePipeline(_pipeline);
            _backend.DestroyBuffer(_buffer);
        }
    }
}
