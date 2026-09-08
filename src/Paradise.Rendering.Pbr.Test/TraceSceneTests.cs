using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Paradise.Rendering.Pbr.Test.Baseline;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

public class TraceSceneTests
{
    [Test]
    public async Task unchanged_hierarchy_is_reused_but_mutable_inputs_invalidate_it()
    {
        WebGpuRenderer backend;
        try
        {
            backend = WebGpuRenderer.CreateHeadless(16, 16);
        }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test(error.Message);
            return;
        }
        using var renderer = backend;
        var recording = new RecordingRenderer(backend) { RecordBufferUpdates = true };
        using var pbr = new PbrRenderer(recording, new FeatureSwitches(), 16, 16);
        using var trace = new TraceScene(recording, NullLogger.Instance);
        var (vertices, indices) = Procedural.UnitCube();
        var material = pbr.Materials.AddDefaultMaterial(Vector4.One);
        var otherMaterial = pbr.Materials.AddDefaultMaterial(new Vector4(1, 0, 0, 1));
        var primitive = pbr.UploadPrimitive(vertices, indices, material);
        var traceMesh = trace.AddMesh(vertices, 12, indices);
        primitive = primitive with { TraceMesh = traceMesh };
        var instance = new PbrInstance { Mesh = new PbrMesh([primitive]) };
        var opaque = new List<(PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth)> { (instance, primitive, 0) };

        void Build()
        {
            recording.BufferUpdates.Clear();
            trace.BuildFrame(opaque, pbr.Materials);
        }
        bool WroteInstances() => recording.BufferUpdates.Any(u => u.ElementType == typeof(TraceInstanceGpu));
        TraceInstanceGpu UploadedInstance() => MemoryMarshal.Read<TraceInstanceGpu>(
            recording.BufferUpdates.Single(u => u.ElementType == typeof(TraceInstanceGpu)).Data);

        Build();
        await Assert.That(WroteInstances()).IsTrue();
        Build();
        await Assert.That(recording.BufferUpdates.All(u => u.ElementType == typeof(TraceMaterialGpu))).IsTrue();

        opaque[0] = (instance, primitive, 42);
        pbr.Materials.AddDefaultMaterial(new Vector4(0, 1, 0, 1));
        Build();
        await Assert.That(WroteInstances()).IsFalse();
        await Assert.That(recording.BufferUpdates.Single().Data.Length).IsEqualTo(3 * 32);

        instance.Model = Matrix4x4.CreateTranslation(3, 0, 0);
        Build();
        await Assert.That(UploadedInstance().WorldToObject.M41).IsEqualTo(-3f);
        await Assert.That(trace.SceneBounds.Min.X).IsGreaterThan(2f);

        opaque[0] = (instance, primitive with { MaterialId = otherMaterial }, 0);
        Build();
        await Assert.That(UploadedInstance().Material).IsEqualTo((uint)otherMaterial);

        var oldRoot = trace.TlasRoot;
        var otherMesh = trace.AddMesh(vertices, 12, indices);
        Build();
        await Assert.That(trace.TlasRoot).IsGreaterThan(oldRoot);
        opaque[0] = (instance, primitive with { TraceMesh = otherMesh }, 0);
        Build();
        await Assert.That(UploadedInstance().RootNode).IsGreaterThan(0u);

        instance.GiMode = PbrGiMode.Dynamic;
        Build();
        await Assert.That(trace.InstanceCount).IsEqualTo(0);
        instance.GiMode = PbrGiMode.Static;
        Build();
        await Assert.That(trace.InstanceCount).IsEqualTo(1);
        instance.Model = Matrix4x4.CreateScale(0);
        Build();
        await Assert.That(trace.InstanceCount).IsEqualTo(0);
        instance.Model = Matrix4x4.Identity;
        Build();
        await Assert.That(trace.InstanceCount).IsEqualTo(1);
        opaque.Clear();
        Build();
        await Assert.That(trace.InstanceCount).IsEqualTo(0);
        await Assert.That(trace.SceneBounds.IsEmpty).IsTrue();
    }
}
