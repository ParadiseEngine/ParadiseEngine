using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Rendering.Graph;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

public class GiLightHierarchyTests
{
    private const int QueryCount = 256;

    private static PbrLight[] Lights() => Enumerable.Range(0, 64).Select(slot => new PbrLight
    {
        Type = slot % 3 == 0 ? PbrLightType.Spot : PbrLightType.Point,
        Position = new Vector3((slot % 8) * 20 - 80, slot / 8 * 15 - 60, slot % 5 * 18 - 36),
        Range = 4 + slot % 4,
        IndirectEnergy = slot % 11 == 0 ? 0 : 1,
    }).ToArray();

    private static Vector4[] Positions(PbrLight[] lights)
    {
        var random = new Random(71);
        return Enumerable.Range(0, QueryCount).Select(index =>
        {
            var light = lights[index % lights.Length];
            var offset = new Vector3((float)random.NextDouble() * 2 - 1,
                (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1);
            return new Vector4(light.Position + offset * light.Range * (index < 128 ? 0.5f : 5f), 0);
        }).ToArray();
    }

    private static ulong BruteForce(IReadOnlyList<PbrLight> lights, Vector3 position)
    {
        var mask = 0ul;
        for (var slot = 0; slot < Math.Min(lights.Count, FrameUniformsGpu.MaxSceneLights); slot++)
        {
            var light = lights[slot];
            if (light.IndirectEnergy <= 0 || light.Intensity == 0 || light.Color == Vector3.Zero) continue;
            if (light.Type == PbrLightType.Directional || light.Range <= 0
                || Vector3.Distance(position, light.Position) <= light.Range)
                mask |= 1ul << slot;
        }
        return mask;
    }

    [Test]
    public async Task world_space_queries_retain_every_contributing_light_and_prune_distant_subtrees()
    {
        var lights = Lights();
        var hierarchy = new GiLightHierarchy();
        hierarchy.Build(lights);
        var contributing = 0;
        var candidates = 0;
        var visited = 0;
        foreach (var query in Positions(lights))
        {
            var position = new Vector3(query.X, query.Y, query.Z);
            var expected = BruteForce(lights, position);
            var mask = hierarchy.Query(position, out var nodes);
            if ((mask & expected) != expected)
                throw new InvalidOperationException($"Query at {position} lost lights: expected {expected:x16}, got {mask:x16}.");
            contributing += BitOperations.PopCount(expected);
            candidates += BitOperations.PopCount(mask);
            visited += nodes;
        }
        await Assert.That(contributing).IsGreaterThan(100);
        await Assert.That(candidates).IsLessThan(QueryCount * 3);
        await Assert.That(visited).IsLessThan(QueryCount * lights.Length / 2);
    }

    [Test]
    public async Task directionals_unlimited_local_ranges_and_signed_energy_preserve_their_slots()
    {
        PbrLight[] lights =
        [
            new() { Type = PbrLightType.Directional },
            new() { Type = PbrLightType.Point, Range = 0 },
            new() { Type = PbrLightType.Spot, Range = -2 },
            new() { Type = PbrLightType.Directional, IndirectEnergy = -1 },
            new() { Type = PbrLightType.Directional, IndirectEnergy = 0 },
            new() { Type = PbrLightType.Directional, Intensity = 0 },
            new() { Type = PbrLightType.Directional, Color = Vector3.Zero },
            new() { Type = PbrLightType.Directional, Intensity = -2 },
            new() { Type = PbrLightType.Directional, Color = -Vector3.One },
        ];
        var hierarchy = new GiLightHierarchy();
        hierarchy.Build(lights);
        await Assert.That(hierarchy.Query(new Vector3(1_000_000), out var visited)).IsEqualTo(0x187ul);
        await Assert.That(visited).IsEqualTo(0);
        hierarchy.Build([]);
        await Assert.That(hierarchy.Query(Vector3.Zero, out _)).IsEqualTo(0ul);
    }

    [Test]
    public async Task range_boundaries_and_large_world_coordinates_are_conservative()
    {
        foreach (var centre in new[] { Vector3.Zero, new Vector3(10000, -70000, 1000000) })
        {
            var hierarchy = new GiLightHierarchy();
            var light = new PbrLight { Type = PbrLightType.Point, Position = centre, Range = 3.7f };
            hierarchy.Build([light]);
            foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            {
                await Assert.That(hierarchy.Query(centre + axis * light.Range, out _)).IsEqualTo(1ul);
                await Assert.That(hierarchy.Query(centre - axis * light.Range, out _)).IsEqualTo(1ul);
                await Assert.That(hierarchy.Query(centre + axis * light.Range * 2, out _)).IsEqualTo(0ul);
            }
        }
    }

    [Test]
    public async Task rebuilding_updates_moving_lights_and_obeys_the_frame_admission_limit()
    {
        var lights = Enumerable.Range(0, 65).Select(slot => new PbrLight
        {
            Type = PbrLightType.Point, Position = new Vector3(slot * 10, 0, 0), Range = 1,
        }).ToArray();
        var hierarchy = new GiLightHierarchy();
        hierarchy.Build(lights);
        await Assert.That(hierarchy.Query(lights[63].Position, out _)).IsEqualTo(1ul << 63);
        await Assert.That(hierarchy.Query(lights[64].Position, out _)).IsEqualTo(0ul);
        lights[63] = lights[63] with { Position = new Vector3(-100) };
        hierarchy.Build(lights);
        await Assert.That(hierarchy.Query(new Vector3(630, 0, 0), out _)).IsEqualTo(0ul);
        await Assert.That(hierarchy.Query(lights[63].Position, out _)).IsEqualTo(1ul << 63);
    }

    [Test]
    public async Task culling_preserves_the_rendered_gi_picture_exactly()
    {
        WebGpuRenderer backend;
        try { backend = WebGpuRenderer.CreateHeadless(96, 96); }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No WebGPU adapter available: {error.Message}");
            return;
        }
        using var lifetime = backend;
        var culled = RenderRoom(backend, culling: true, intensity: 1);
        var unculled = RenderRoom(backend, culling: false, intensity: 1);
        var noBounce = RenderRoom(backend, culling: true, intensity: 0);
        await Assert.That(culled.SequenceEqual(unculled)).IsTrue();
        await Assert.That(culled.SequenceEqual(noBounce)).IsFalse();
        await Assert.That(culled.Where((_, index) => index % 4 != 3).Any(channel => channel > 20)).IsTrue();
    }

    private static byte[] RenderRoom(WebGpuRenderer backend, bool culling, float intensity)
    {
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 96, 96);
        var scene = LitRoom(pbr);
        var gi = pbr.Pipeline.Find<ProbeGiFeature>()!;
        gi.Settings = gi.Settings with { LightCullingEnabled = culling, Intensity = intensity };
        for (var frame = 0; frame < 8; frame++) pbr.RenderFrame(scene);
        return (byte[])backend.ReadbackColor(out _, out _).Clone();
    }

    private static PbrScene LitRoom(PbrRenderer pbr)
    {
        var eye = new Vector3(0, 1, 3);
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                View = PbrMath.LookAt(eye, new Vector3(0, 1, -3), Vector3.UnitY),
                Projection = PbrMath.Perspective(MathF.PI / 3, 1, 0.1f, 100),
                Position = eye,
            },
            Ambient = new PbrAmbient { Flat = true, Sky = Vector3.Zero },
            Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Linear },
        };
        var (vertices, indices) = Procedural.UnitCube();
        var material = pbr.Materials.AddDefaultMaterial(new Vector4(0.6f, 0.6f, 0.6f, 1));
        var mesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, material)]);
        foreach (var (scale, position) in new[]
        {
            (new Vector3(8, 0.2f, 8), new Vector3(0, -0.1f, 0)),
            (new Vector3(8, 4, 0.2f), new Vector3(0, 2, -4)),
            (new Vector3(0.2f, 4, 8), new Vector3(-4, 2, 0)),
            (new Vector3(0.2f, 4, 8), new Vector3(4, 2, 0)),
        })
            scene.Instances.Add(new PbrInstance { Mesh = mesh, Model = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(position) });
        scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Point, Position = new Vector3(0, 2, 0), Range = 6, Intensity = 4,
            Color = new Vector3(0.7f, 0.5f, 0.3f), CastsShadows = true,
        });
        for (var slot = 1; slot < 64; slot++)
            scene.Lights.Add(new PbrLight
            {
                Type = slot % 2 == 0 ? PbrLightType.Spot : PbrLightType.Point,
                Position = new Vector3((slot % 8 - 4) * 12, 2, (slot / 8 - 4) * 12),
                Range = 5, Intensity = 3,
            });
        pbr.Pipeline.Find<ProbeGiFeature>()!.Settings = new PbrGi
        {
            Enabled = true, RaysPerProbe = 64, Hysteresis = 0.4f,
            Volume = new PbrProbeVolume(new Vector3(-3, 0.5f, -3), new Vector3(1.5f, 1, 1.5f), 5, 3, 5),
        };
        return scene;
    }

    [Test]
    public async Task gpu_queries_match_the_cpu_tree_at_offscreen_world_positions()
    {
        WebGpuRenderer backend;
        try { backend = WebGpuRenderer.CreateHeadless(16, 16); }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No WebGPU adapter available: {error.Message}");
            return;
        }
        using var lifetime = backend;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var lights = Lights();
        lights[11] = lights[11] with { Type = PbrLightType.Directional, IndirectEnergy = 1 };
        lights[33] = lights[33] with { Range = 0, IndirectEnergy = 1 };
        var queries = Positions(lights);
        var hierarchy = new GiLightHierarchy();
        hierarchy.Build(lights);
        var feature = new QueryFeature(backend, hierarchy, queries);
        pbr.Pipeline.Add(feature, PbrFeatureOrder.GlobalIllumination + 1);
        pbr.RenderFrame(new PbrScene
        {
            Camera = new PbrCamera { View = Matrix4x4.Identity, Projection = Matrix4x4.Identity },
        });
        var actual = MemoryMarshal.Cast<byte, ulong>(backend.ReadbackBuffer(feature.Output, 0, QueryCount * 8)).ToArray();
        for (var index = 0; index < queries.Length; index++)
        {
            var query = queries[index];
            var expected = hierarchy.Query(new Vector3(query.X, query.Y, query.Z), out _);
            if (actual[index] != expected)
                throw new InvalidOperationException($"GPU query {index}: expected {expected:x16}, got {actual[index]:x16}.");
        }
        await Assert.That(actual.Any(mask => BitOperations.PopCount(mask) > 2)).IsTrue();
        await Assert.That(Unsafe.SizeOf<GiLightNodeGpu>()).IsEqualTo(32);
        await Assert.That(Unsafe.SizeOf<GiLightTreeGpu>()).IsEqualTo(4080);
    }

    private sealed class QueryFeature : IRenderFeature
    {
        private readonly WebGpuRenderer _renderer;
        private readonly ShaderProgramDesc _program;
        private readonly ComputePipelineHandle _pipeline;
        private readonly BufferHandle _tree;
        private readonly BufferHandle _positions;
        public BufferHandle Output { get; }

        public QueryFeature(WebGpuRenderer renderer, GiLightHierarchy hierarchy, Vector4[] positions)
        {
            _renderer = renderer;
            _program = ShaderProgramLoader.Load(typeof(GiLightHierarchyTests).Assembly, "Shaders.giLightFixture");
            _pipeline = renderer.CreateComputePipeline(_program);
            _tree = renderer.CreateBuffer(new BufferDesc("Test.GiLightTree", 4080, BufferUsage.Uniform | BufferUsage.CopyDst));
            _positions = renderer.CreateBuffer(new BufferDesc("Test.GiLightPositions", QueryCount * 16, BufferUsage.Storage | BufferUsage.CopyDst));
            Output = renderer.CreateBuffer(new BufferDesc("Test.GiLightMasks", QueryCount * 8, BufferUsage.Storage | BufferUsage.CopySrc));
            ref readonly var tree = ref hierarchy.Data;
            renderer.UpdateBuffer<GiLightTreeGpu>(_tree, 0, MemoryMarshal.CreateReadOnlySpan(in tree, 1));
            renderer.UpdateBuffer<Vector4>(_positions, 0, positions);
        }

        public FeatureDefinition Definition { get; } = new("test.giLightQueries", true);
        public FrameRequirements Requires => FrameRequirements.None;
        public void Resize(uint width, uint height) { }
        public void Setup(in FrameContext frame)
        {
            frame.Graph.AddComputePass("Test.GiLightQueries", RenderPassEvent.GlobalIllumination)
                .BindGroup(0, "Test.GiLightQueries", ShaderPrograms.FindGroup(_program, 0),
                [
                    GraphBinding.Buffer(0, Output, 0, QueryCount * 8),
                    GraphBinding.Buffer(1, _tree, 0, 4080),
                    GraphBinding.Buffer(2, _positions, 0, QueryCount * 16),
                ])
                .NeverCull()
                .Record(this, Record);
        }

        private static void Record(QueryFeature self, ref PassRecording pass, int _)
        {
            pass.Encoder.SetComputePipeline(self._pipeline);
            pass.SetBindGroup(0);
            pass.Encoder.Dispatch(new DispatchCommand(QueryCount / 64, 1, 1));
        }

        public void Dispose()
        {
            _renderer.DestroyComputePipeline(_pipeline);
            _renderer.DestroyBuffer(_tree);
            _renderer.DestroyBuffer(_positions);
            _renderer.DestroyBuffer(Output);
        }
    }
}
