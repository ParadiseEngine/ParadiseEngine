using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Rendering.Graph;
using Paradise.Rendering.Pbr.Test.Baseline;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

public class LightingFrameDataTests
{
    private const uint Size = 64;

    private sealed class LightingConsumer : IRenderFeature
    {
        public FeatureDefinition Definition { get; } = new("test.lightingConsumer", true, "Consumes published lighting data.");
        public FrameRequirements Requires => FrameRequirements.None;
        public bool ConsumeGrid { get; set; }
        public bool SawShadows { get; private set; }
        public bool SawGrid { get; private set; }
        public ShadowFrameData Shadows { get; private set; }
        public LightGridFrameData Grid { get; private set; }

        public void Setup(in FrameContext frame)
        {
            SawShadows = frame.Blackboard.TryGet(ShadowFrameData.Key, out var shadows);
            SawGrid = frame.Blackboard.TryGet(LightGridFrameData.Key, out var grid);
            Shadows = shadows;
            Grid = grid;
            if (ConsumeGrid && SawGrid)
                frame.Graph.AddRasterPass("Host.LightGridReader", RenderPassEvent.Overlay)
                    .Color(0, FrameGraph.Backbuffer, LoadOp.Clear, clear: new ColorRgba(0, 0, 0, 1))
                    .Reads(grid.Masks)
                    .Record(this, Record);
        }

        private static void Record(LightingConsumer _, ref PassRecording pass, int value) { }
        public void Resize(uint width, uint height) { }
        public void Dispose() { }
    }

    private sealed class InstancePlanConsumer : IRenderFeature
    {
        public FeatureDefinition Definition { get; } = new("test.instancePlanConsumer", true, "Consumes published instance resources.");
        public FrameRequirements Requires => FrameRequirements.None;
        public InstanceDrawPlan Plan { get; private set; }

        public void Setup(in FrameContext frame)
        {
            Plan = frame.Blackboard.GetOrDefault(InstanceDrawPlan.Key, default);
            if (!Plan.Group.IsValid) return;
            frame.Graph.AddRasterPass("Host.InstancePlanReader", RenderPassEvent.Overlay)
                .Color(0, FrameGraph.Backbuffer, LoadOp.Clear, clear: new ColorRgba(0, 0, 0, 1))
                .Record(this, Record);
        }

        private static void Record(InstancePlanConsumer self, ref PassRecording pass, int _) =>
            pass.Encoder.SetBindGroup(0, self.Plan.Group);
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

    private static PbrScene Scene(PbrRenderer pbr)
    {
        var eye = new Vector3(2, 2, 4);
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                Position = eye,
                View = PbrMath.LookAt(eye, Vector3.Zero, Vector3.UnitY),
                Projection = PbrMath.Perspective(MathF.PI / 3, 1, 0.1f, 20),
            },
        };
        var (vertices, indices) = Procedural.UnitCube();
        var material = pbr.Materials.AddDefaultMaterial(Vector4.One);
        scene.Instances.Add(new PbrInstance { Mesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, material)]) });
        scene.Lights.Add(new PbrLight { Type = PbrLightType.Directional, Direction = Vector3.UnitY, CastsShadows = true });
        scene.Lights.Add(new PbrLight { Type = PbrLightType.Point, Position = new Vector3(1, 1, 2), Range = 4 });
        return scene;
    }

    [Test]
    public async Task light_grid_compute_lives_only_when_a_graph_consumer_reads_its_published_buffer()
    {
        using var backend = Backend();
        if (backend is null) return;
        var switches = new FeatureSwitches();
        switches.Set(PbrFeatures.Scene.Id, false);
        switches.Set(PbrFeatures.Fog.Id, false);
        switches.Set(PbrFeatures.GlobalIllumination.Id, false);
        switches.Set(PbrFeatures.Shadows.Id, false);
        using var pbr = new PbrRenderer(backend, switches, Size, Size);
        var consumer = new LightingConsumer();
        pbr.Pipeline.Add(consumer);
        var scene = Scene(pbr);

        foreach (var consume in new[] { false, true, false })
        {
            consumer.ConsumeGrid = consume;
            pbr.RenderFrame(scene);
            await Assert.That(consumer.SawGrid).IsTrue();
            await Assert.That(consumer.Grid.Masks.Index).IsGreaterThanOrEqualTo(0);
            await Assert.That(pbr.LastPassNames.Contains("LightCull.Bin")).IsEqualTo(consume);
            await Assert.That(pbr.LastPassNames.Contains("Host.LightGridReader")).IsEqualTo(consume);
        }
    }

    [Test]
    public async Task lighting_results_are_absent_while_disabled_and_invalid_grids_do_not_reuse_old_results()
    {
        using var backend = Backend();
        if (backend is null) return;
        var switches = new FeatureSwitches();
        switches.Set(PbrFeatures.Shadows.Id, false);
        switches.Set(PbrFeatures.LightCulling.Id, false);
        using var pbr = new PbrRenderer(backend, switches, Size, Size);
        var consumer = new LightingConsumer();
        pbr.Pipeline.Add(consumer);
        var scene = Scene(pbr);

        foreach (var enabled in new[] { false, true, false, true })
        {
            switches.Set(PbrFeatures.Shadows.Id, enabled);
            switches.Set(PbrFeatures.LightCulling.Id, enabled);
            pbr.RenderFrame(scene);
            await Assert.That(consumer.SawShadows).IsEqualTo(enabled);
            await Assert.That(consumer.SawGrid).IsEqualTo(enabled);
            if (enabled)
            {
                await Assert.That(consumer.Shadows.Atlas.Index).IsGreaterThanOrEqualTo(0);
                await Assert.That(consumer.Shadows.Sampler.IsValid).IsTrue();
                await Assert.That(consumer.Shadows.Views.Count).IsGreaterThan(0);
                await Assert.That(consumer.Shadows.FirstView(0)).IsEqualTo(0);
                await Assert.That(consumer.Shadows.ViewCount(0)).IsGreaterThan(0);
                await Assert.That(consumer.Shadows.TexelWorld(0)).IsGreaterThan(0);
                await Assert.That(consumer.Grid.Masks.Index).IsGreaterThanOrEqualTo(0);
                await Assert.That(consumer.Grid.BufferBytes).IsGreaterThan(0ul);
            }
            else
            {
                await Assert.That(consumer.Shadows.FirstView(0)).IsEqualTo(-1);
                await Assert.That(consumer.Shadows.ViewCount(0)).IsEqualTo(0);
                await Assert.That(consumer.Shadows.TexelWorld(0)).IsEqualTo(0f);
            }
        }

        var finite = scene.Camera.Projection;
        scene.Camera = scene.Camera with { Projection = PbrMath.Perspective(MathF.PI / 3, 1, 0.1f, float.PositiveInfinity) };
        pbr.RenderFrame(scene);
        await Assert.That(consumer.SawGrid).IsFalse();
        await Assert.That(consumer.SawShadows).IsTrue();
        await Assert.That(pbr.LastPassNames.Contains("LightCull.Bin")).IsFalse();

        scene.Camera = scene.Camera with { Projection = finite };
        pbr.RenderFrame(scene);
        await Assert.That(consumer.SawGrid).IsTrue();
        await Assert.That(pbr.LastPassNames.Contains("LightCull.Bin")).IsTrue();
    }

    [Test]
    public async Task published_instance_resources_upload_current_data_without_the_builtin_scene_consumer()
    {
        using var backend = Backend();
        if (backend is null) return;
        var switches = new FeatureSwitches();
        switches.Set(PbrFeatures.Scene.Id, false);
        switches.Set(PbrFeatures.GlobalIllumination.Id, false);
        switches.Set(PbrFeatures.Shadows.Id, false);
        var recording = new RecordingRenderer(backend) { RecordBufferUpdates = true, RecordBindGroups = true };
        using var pbr = new PbrRenderer(recording, switches, Size, Size);
        var consumer = new InstancePlanConsumer();
        pbr.Pipeline.Add(consumer);
        var scene = Scene(pbr);
        var second = new PbrInstance { Mesh = scene.Instances[0].Mesh };
        var previousBuffer = default(BufferHandle);

        foreach (var batched in new[] { true, false, true })
        {
            if (batched) scene.Instances.Add(second);
            else scene.Instances.Remove(second);
            second.Model *= Matrix4x4.CreateTranslation(0.2f, 0.1f, 0);
            second.Highlight += 0.25f;
            recording.BufferUpdates.Clear();
            pbr.RenderFrame(scene);
            await Assert.That(pbr.Pipeline.Find<InstancingFeature>()!.BatchedInstances).IsEqualTo(0);
            await Assert.That(consumer.Plan.Group.IsValid).IsEqualTo(batched);
            if (!batched)
            {
                await Assert.That(recording.BufferUpdates.Any(update => update.Buffer == previousBuffer)).IsFalse();
                continue;
            }

            await Assert.That(consumer.Plan.Batch(opaque: true, 0).Count).IsEqualTo(2);
            await Assert.That(pbr.LastPassNames.Contains("Host.InstancePlanReader")).IsTrue();
            var group = recording.BindGroups[consumer.Plan.Group];
            var buffer = group.Entries.ToArray().Single(entry => entry.Binding == 1).Buffer;
            var uploaded = recording.BufferUpdates.Single(update => update.Buffer == buffer);
            var draws = MemoryMarshal.Cast<byte, DrawUniformsGpu>(uploaded.Data).ToArray();
            await Assert.That(draws.Length).IsEqualTo(2);
            await Assert.That(draws[0].Model).IsEqualTo(scene.Instances[0].Model);
            await Assert.That(draws[1].Model).IsEqualTo(second.Model);
            await Assert.That(draws[1].Highlight.X).IsEqualTo(second.Highlight);
            previousBuffer = buffer;
        }
    }
}
