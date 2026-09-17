using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Paradise.Assets.Gltf;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr.Test;

public class GeometryLifecycleTests
{
    private sealed class CallbackFeature(Action callback) : IRenderFeature
    {
        public FeatureDefinition Definition { get; } = new("test.resourceMutation", true, "Tests resource lifetime boundaries.");
        public FrameRequirements Requires => FrameRequirements.None;
        public void Setup(in FrameContext frame) => callback();
        public void Resize(uint width, uint height) { }
        public void Dispose() { }
    }

    [Test]
    public async Task material_variants_share_geometry_release_and_other_uploads_remain_live()
    {
        var backend = new ResourceTrackingRenderer();
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var (vertices, indices) = Procedural.UnitCube();
        var material = pbr.Materials.AddDefaultMaterial(Vector4.One);
        var primitive = pbr.UploadPrimitive(vertices, indices, material, dynamic: true);
        var variant = primitive with { MaterialId = pbr.Materials.AddDefaultMaterial(new Vector4(1, 0, 0, 1)) };
        var survivor = pbr.UploadPrimitive(vertices, indices, material);

        await Assert.That(pbr.ReleasePrimitive(variant)).IsTrue();
        await Assert.That(pbr.ReleasePrimitive(primitive)).IsFalse();
        await Assert.That(backend.DestroyedBuffers[primitive.VertexBuffer]).IsEqualTo(1);
        await Assert.That(backend.DestroyedBuffers[primitive.IndexBuffer]).IsEqualTo(1);
        await Assert.That(backend.Buffers.ContainsKey(survivor.VertexBuffer)).IsTrue();
        await Assert.That(pbr.Materials.MaterialCount).IsEqualTo(2);
        await Assert.That(() => pbr.UpdatePrimitiveVertices(variant, vertices)).Throws<ObjectDisposedException>();
        var scene = new PbrScene();
        scene.Instances.Add(new PbrInstance { Mesh = new PbrMesh([variant]) });
        await Assert.That(() => pbr.RenderFrame(scene)).Throws<ObjectDisposedException>();
        scene.Instances.Clear();
        scene.GiGeometry.Instances.Add(new PbrInstance { Mesh = new PbrMesh([variant]) });
        await Assert.That(() => pbr.RenderFrame(scene)).Throws<ObjectDisposedException>();

        pbr.Dispose();
        await Assert.That(pbr.ReleasePrimitive(survivor)).IsFalse();
        await Assert.That(backend.ResourceCount).IsEqualTo(0);
    }

    [Test]
    public async Task foreign_or_rewritten_handles_cannot_release_another_upload()
    {
        var backend = new ResourceTrackingRenderer();
        using var first = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        using var second = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var (vertices, indices) = Procedural.UnitCube();
        var primitive = first.UploadPrimitive(vertices, indices, first.Materials.AddDefaultMaterial(Vector4.One));
        var other = second.UploadPrimitive(vertices, indices, second.Materials.AddDefaultMaterial(Vector4.One));

        await Assert.That(() => second.ReleasePrimitive(primitive)).Throws<ArgumentException>();
        await Assert.That(() => first.ReleasePrimitive(primitive with { VertexBuffer = other.VertexBuffer })).Throws<ArgumentException>();
        await Assert.That(backend.Buffers.ContainsKey(primitive.VertexBuffer)).IsTrue();
        await Assert.That(backend.Buffers.ContainsKey(other.VertexBuffer)).IsTrue();
        await Assert.That(first.ReleasePrimitive(primitive)).IsTrue();
        await Assert.That(second.ReleasePrimitive(other)).IsTrue();
    }

    [Test]
    public async Task failed_primitive_upload_rolls_back_allocations_and_accepts_a_later_upload()
    {
        var backend = new ResourceTrackingRenderer();
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var (vertices, indices) = Procedural.UnitCube();
        var material = pbr.Materials.AddDefaultMaterial(Vector4.One);
        var resourceCount = backend.ResourceCount;
        backend.FailNextBufferName = "PbrIndices";
        await Assert.That(() => pbr.UploadPrimitive(vertices, indices, material)).Throws<InvalidOperationException>();
        await Assert.That(backend.ResourceCount).IsEqualTo(resourceCount);
        await Assert.That(() => pbr.UploadPrimitive(vertices, [uint.MaxValue, 0, 1], material)).Throws<ArgumentException>();
        await Assert.That(() => pbr.UploadSkinnedPrimitive(vertices[..^1], [], indices, material)).Throws<ArgumentException>();
        await Assert.That(backend.ResourceCount).IsEqualTo(resourceCount);
        var primitive = pbr.UploadPrimitive(vertices, indices, material);
        await Assert.That(primitive.TraceMesh).IsGreaterThan(0);
        await Assert.That(pbr.ReleasePrimitive(primitive)).IsTrue();
        await Assert.That(backend.ResourceCount).IsEqualTo(resourceCount);
    }

    [Test]
    public async Task failed_mesh_upload_releases_previously_uploaded_geometry_and_its_fallback_material()
    {
        var backend = new ResourceTrackingRenderer();
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var (vertices, indices) = Procedural.UnitCube();
        var good = new GltfPrimitive(vertices, indices, -1, true, true, true);
        var asset = new GltfAsset([], [new GltfMeshData("partial", [good, good with { Indices = [0, 1] }])], [], [], [], [], []);
        var resourceCount = backend.ResourceCount;
        await Assert.That(() => pbr.UploadMesh(asset)).Throws<ArgumentException>();
        await Assert.That(pbr.Materials.MaterialCount).IsEqualTo(0);
        await Assert.That(backend.ResourceCount).IsEqualTo(resourceCount);
    }

    [Test]
    [Arguments("release")]
    [Arguments("upload")]
    [Arguments("dispose")]
    public async Task resource_mutation_during_a_frame_is_rejected_without_leaving_the_frame_guard_set(string operation)
    {
        var backend = new ResourceTrackingRenderer();
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var (vertices, indices) = Procedural.UnitCube();
        var material = pbr.Materials.AddDefaultMaterial(Vector4.One);
        var primitive = pbr.UploadPrimitive(vertices, indices, material);
        pbr.Pipeline.Add(new CallbackFeature(() =>
        {
            switch (operation)
            {
                case "release": pbr.ReleasePrimitive(primitive); break;
                case "upload": pbr.UploadPrimitive(vertices, indices, material); break;
                case "dispose": pbr.Dispose(); break;
            }
        }), PbrFeatureOrder.First);
        var scene = new PbrScene();
        scene.Instances.Add(new PbrInstance { Mesh = new PbrMesh([primitive]) });
        await Assert.That(() => pbr.RenderFrame(scene)).Throws<InvalidOperationException>().WithMessageContaining("between frames");
        await Assert.That(pbr.ReleasePrimitive(primitive)).IsTrue();
    }

    [Test]
    public async Task trace_removal_reclaims_buffers_rebases_survivors_and_never_reuses_stale_ids()
    {
        var backend = new ResourceTrackingRenderer();
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        using var trace = new TraceScene(backend, NullLogger.Instance);
        using var reference = new TraceScene(backend, NullLogger.Instance);
        var (vertices, indices) = Procedural.UnitCube();
        var material = pbr.Materials.AddDefaultMaterial(Vector4.One);
        var removed = trace.AddMesh(vertices, 12, indices);
        var survivor = trace.AddMesh(vertices, 12, indices);
        var last = trace.AddMesh(vertices, 12, indices);
        var primitive = new PbrPrimitive(default, default, (uint)indices.Length, 0, 0, material, TraceMesh: survivor);
        var instance = new PbrInstance { Mesh = new PbrMesh([primitive]) };
        var draws = new List<FrameDraw> { (instance, primitive, 0) };
        trace.BuildFrame(draws, pbr.Materials);
        var oldBuffers = trace.Bindings().Take(3).Select(binding => binding.Raw.Buffer).ToArray();
        await Assert.That(trace.RemoveMesh(removed)).IsTrue();
        await Assert.That(trace.RemoveMesh(last)).IsTrue();
        await Assert.That(trace.RemoveMesh(removed)).IsFalse();
        await Assert.That(oldBuffers.All(buffer => !backend.Buffers.ContainsKey(buffer))).IsTrue();
        var added = trace.AddMesh(vertices, 12, indices);
        await Assert.That(added).IsGreaterThan(last);
        await Assert.That(trace.RemoveMesh(added)).IsTrue();
        await Assert.That(trace.MeshCount).IsEqualTo(1);

        trace.BuildFrame(draws, pbr.Materials);
        var referenceId = reference.AddMesh(vertices, 12, indices);
        reference.BuildFrame([(instance, primitive with { TraceMesh = referenceId }, 0)], pbr.Materials);
        for (var slot = 0; slot < 4; slot++)
        {
            var actual = backend.BufferData[trace.Bindings()[slot].Raw.Buffer];
            var expected = backend.BufferData[reference.Bindings()[slot].Raw.Buffer];
            await Assert.That(actual.AsSpan().SequenceEqual(expected)).IsTrue();
        }
        await Assert.That(() => trace.BuildFrame([(instance, primitive with { TraceMesh = removed }, 0)], pbr.Materials))
            .Throws<ArgumentException>().WithMessageContaining("not live");
    }

    [Test]
    public async Task trace_material_storage_stays_bounded_when_material_ids_are_retired()
    {
        var backend = new ResourceTrackingRenderer();
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        using var trace = new TraceScene(backend, NullLogger.Instance);
        var (vertices, indices) = Procedural.UnitCube();
        var mesh = trace.AddMesh(vertices, 12, indices);
        var primitive = new PbrPrimitive(default, default, (uint)indices.Length, 0, 0, -1, TraceMesh: mesh);
        var instance = new PbrInstance { Mesh = new PbrMesh([primitive]) };
        var previousId = -1;
        var materialBuffer = default(BufferHandle);
        for (var i = 0; i < 64; i++)
        {
            var color = new Vector4(i / 64f, 0.25f, 0.75f, 1);
            var material = pbr.Materials.AddDefaultMaterial(color);
            await Assert.That(material).IsGreaterThan(previousId);
            trace.BuildFrame([(instance, primitive with { MaterialId = material }, 0)], pbr.Materials);
            var bindings = trace.Bindings();
            var uploadedInstance = MemoryMarshal.Read<TraceInstanceGpu>(backend.BufferData[bindings[3].Raw.Buffer]);
            var uploadedMaterial = MemoryMarshal.Read<TraceMaterialGpu>(backend.BufferData[bindings[4].Raw.Buffer]);
            await Assert.That(uploadedInstance.Material).IsEqualTo(0u);
            await Assert.That(uploadedMaterial.BaseColor.X).IsEqualTo(color.X);
            await Assert.That(uploadedMaterial.BaseColor.Y).IsEqualTo(color.Y);
            await Assert.That(backend.Buffers[bindings[4].Raw.Buffer].Size).IsEqualTo(32ul);
            if (i > 0) await Assert.That(bindings[4].Raw.Buffer).IsEqualTo(materialBuffer);
            materialBuffer = bindings[4].Raw.Buffer;
            await Assert.That(pbr.Materials.ReleaseMaterial(material)).IsTrue();
            previousId = material;
        }
        trace.BuildFrame([], pbr.Materials);
        await Assert.That(trace.InstanceCount).IsEqualTo(0);
    }

    [Test]
    public async Task changed_material_mapping_updates_an_unchanged_gi_participant()
    {
        var backend = new ResourceTrackingRenderer();
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        using var trace = new TraceScene(backend, NullLogger.Instance);
        var (vertices, indices) = Procedural.UnitCube();
        var mesh = trace.AddMesh(vertices, 12, indices);
        var visibleMaterial = pbr.Materials.AddDefaultMaterial(new Vector4(1, 0, 0, 1));
        var giMaterial = pbr.Materials.AddDefaultMaterial(new Vector4(0, 1, 0, 1));
        var visiblePrimitive = new PbrPrimitive(default, default, (uint)indices.Length, 0, 0, visibleMaterial, TraceMesh: mesh);
        var giPrimitive = visiblePrimitive with { MaterialId = giMaterial };
        var visible = new PbrInstance { Mesh = new PbrMesh([visiblePrimitive]) };
        var scene = new PbrScene();
        scene.GiGeometry.IncludeSceneInstances = false;
        scene.GiGeometry.Instances.Add(new PbrInstance { Mesh = new PbrMesh([giPrimitive]) });
        trace.BuildFrame([(visible, visiblePrimitive, 0)], pbr.Materials, scene);
        var giBuffer = trace.Bindings(globalIllumination: true)[3].Raw.Buffer;
        await Assert.That(MemoryMarshal.Read<TraceInstanceGpu>(backend.BufferData[giBuffer]).Material).IsEqualTo(1u);

        trace.BuildFrame([], pbr.Materials, scene);
        await Assert.That(trace.Bindings(globalIllumination: true)[3].Raw.Buffer).IsEqualTo(giBuffer);
        await Assert.That(MemoryMarshal.Read<TraceInstanceGpu>(backend.BufferData[giBuffer]).Material).IsEqualTo(0u);
        var materialBuffer = trace.Bindings()[4].Raw.Buffer;
        var material = MemoryMarshal.Read<TraceMaterialGpu>(backend.BufferData[materialBuffer]);
        await Assert.That(material.BaseColor.X).IsEqualTo(0f);
        await Assert.That(material.BaseColor.Y).IsEqualTo(1f);
        await Assert.That(backend.Buffers[materialBuffer].Size).IsEqualTo(64ul);
    }
}
