using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Paradise.Assets.Gltf;
using Paradise.Rendering.Pbr.Test.Baseline;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

public class GiGeometryTests
{
    private const uint Size = 96;

    private static WebGpuRenderer? CreateRenderer()
    {
        try
        {
            return WebGpuRenderer.CreateHeadless(Size, Size);
        }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test(error.Message);
            return null;
        }
    }

    [Test]
    public async Task proxies_and_streamed_instances_share_geometry_without_changing_the_ao_hierarchy()
    {
        using var backend = CreateRenderer();
        if (backend is null) return;
        var recording = new RecordingRenderer(backend) { RecordBufferUpdates = true };
        using var pbr = new PbrRenderer(recording, new FeatureSwitches(), Size, Size);
        using var trace = new TraceScene(recording, NullLogger.Instance);
        var (vertices, indices) = Procedural.UnitCube();
        var material = pbr.Materials.AddDefaultMaterial(Vector4.One);
        var proxyMaterial = pbr.Materials.AddDefaultMaterial(new Vector4(1, 0, 0, 1));
        var primitive = pbr.UploadPrimitive(vertices, indices, material) with { TraceMesh = trace.AddMesh(vertices, 12, indices) };
        var proxy = primitive with { TraceMesh = trace.AddMesh(vertices, 12, indices), MaterialId = proxyMaterial };
        var instance = new PbrInstance { Mesh = new PbrMesh([primitive]) };
        var scene = new PbrScene();
        scene.Instances.Add(instance);
        var opaque = new List<(PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth)> { (instance, primitive, 0) };

        void Build()
        {
            recording.BufferUpdates.Clear();
            trace.BuildFrame(opaque, pbr.Materials, scene);
        }
        TraceInstanceGpu UploadedGi() => MemoryMarshal.Read<TraceInstanceGpu>(recording.BufferUpdates
            .Single(u => u.Buffer == trace.Bindings(globalIllumination: true)[3].Raw.Buffer).Data);

        Build();
        await Assert.That(trace.Bindings().SequenceEqual(trace.Bindings(globalIllumination: true))).IsTrue();
        var visibleBuffer = trace.Bindings()[3].Raw.Buffer;
        instance.GiMesh = new PbrMesh([proxy]);
        Build();
        await Assert.That(UploadedGi().RootNode).IsGreaterThan(0u);
        await Assert.That(UploadedGi().Material).IsEqualTo((uint)proxyMaterial);
        await Assert.That(recording.BufferUpdates.All(u => u.Buffer != visibleBuffer)).IsTrue();
        foreach (var slot in new[] { 0, 1, 2, 4 })
            await Assert.That(trace.Bindings()[slot]).IsEqualTo(trace.Bindings(globalIllumination: true)[slot]);
        await Assert.That(trace.MeshCount).IsEqualTo(2);

        Build();
        await Assert.That(recording.BufferUpdates.All(u => u.ElementType == typeof(TraceMaterialGpu))).IsTrue();
        scene.GiGeometry.IncludeSceneInstances = false;
        scene.GiGeometry.Instances.Add(new PbrInstance
        {
            Mesh = new PbrMesh([proxy]), Model = Matrix4x4.CreateTranslation(100, 0, 0),
        });
        Build();
        await Assert.That(trace.GiInstanceCount).IsEqualTo(1);
        await Assert.That(trace.InstanceCount).IsEqualTo(1);
        await Assert.That(trace.SceneBounds.Min.X).IsGreaterThan(99f);
        await Assert.That(UploadedGi().WorldToObject.M41).IsEqualTo(-100f);
        await Assert.That(recording.BufferUpdates.All(u => u.Buffer != visibleBuffer)).IsTrue();

        scene.GiGeometry.Instances.Clear();
        Build();
        await Assert.That(trace.GiInstanceCount).IsEqualTo(0);
        await Assert.That(trace.InstanceCount).IsEqualTo(1);
        await Assert.That(trace.SceneBounds.IsEmpty).IsTrue();
        instance.GiMesh = null;
        scene.GiGeometry.IncludeSceneInstances = true;
        Build();
        await Assert.That(trace.Bindings().SequenceEqual(trace.Bindings(globalIllumination: true))).IsTrue();

        trace.BuildFrame(opaque, pbr.Materials, scene, rayTracedAo: false);
        await Assert.That(trace.InstanceCount).IsEqualTo(0);
        await Assert.That(trace.GiInstanceCount).IsEqualTo(1);
        trace.BuildFrame(opaque, pbr.Materials, scene);
        await Assert.That(trace.Bindings().SequenceEqual(trace.Bindings(globalIllumination: true))).IsTrue();
    }

    [Test]
    public async Task gi_only_geometry_uses_the_static_and_material_participation_rules()
    {
        using var backend = CreateRenderer();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        using var trace = new TraceScene(backend, NullLogger.Instance);
        var (vertices, indices) = Procedural.UnitCube();
        var opaque = Material(pbr, Vector3.Zero);
        var blended = Material(pbr, Vector3.Zero, GltfAlphaMode.Blend);
        var alphaTested = Material(pbr, Vector3.Zero, GltfAlphaMode.Mask);
        var primitive = pbr.UploadPrimitive(vertices, indices, opaque) with { TraceMesh = trace.AddMesh(vertices, 12, indices) };
        var mesh = new PbrMesh([primitive]);
        var scene = new PbrScene();
        scene.GiGeometry.Instances.Add(new PbrInstance { Mesh = mesh, GiMode = PbrGiMode.Disabled });
        scene.GiGeometry.Instances.Add(new PbrInstance { Mesh = mesh, GiMode = PbrGiMode.Dynamic });
        scene.GiGeometry.Instances.Add(new PbrInstance { Mesh = mesh, Model = Matrix4x4.CreateScale(0) });
        scene.GiGeometry.Instances.Add(new PbrInstance { Mesh = new PbrMesh([primitive with { MaterialId = blended }]) });
        scene.GiGeometry.Instances.Add(new PbrInstance { Mesh = new PbrMesh([primitive with { MaterialId = alphaTested }]) });
        trace.BuildFrame([], pbr.Materials, scene);
        await Assert.That(trace.GiInstanceCount).IsEqualTo(1);
        await Assert.That(trace.InstanceCount).IsEqualTo(0);
    }

    [Test]
    public async Task an_emitter_in_the_gi_only_set_lights_a_rendered_wall()
    {
        using var backend = CreateRenderer();
        if (backend is null) return;
        double dark, lit;
        using (var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size))
            dark = Red(Render(backend, pbr, EmissiveScene(pbr, enabled: false), 3));
        using (var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size))
            lit = Red(Render(backend, pbr, EmissiveScene(pbr, enabled: true), 12));
        await Assert.That(dark).IsLessThan(2.0);
        await Assert.That(lit).IsGreaterThan(12.0);
    }

    [Test]
    public async Task gi_proxies_and_gi_only_occluders_do_not_change_ray_traced_ao_pixels()
    {
        using var backend = CreateRenderer();
        if (backend is null) return;
        byte[] reference, customized;
        using (var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size))
            reference = Render(backend, pbr, AoScene(pbr, customizeGi: false), 3);
        using (var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size))
            customized = Render(backend, pbr, AoScene(pbr, customizeGi: true), 3);
        await Assert.That(customized.SequenceEqual(reference)).IsTrue();
    }

    [Test]
    public async Task distant_gi_only_walls_block_sky_outside_a_bounded_probe_volume()
    {
        using var backend = CreateRenderer();
        if (backend is null) return;
        double open, enclosed;
        using (var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size))
            open = Red(Render(backend, pbr, BoundedScene(pbr, enclosed: false), 16));
        using (var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size))
            enclosed = Red(Render(backend, pbr, BoundedScene(pbr, enclosed: true), 16));
        await Assert.That(open - enclosed).IsGreaterThan(10.0);
    }

    private static PbrScene BoundedScene(PbrRenderer pbr, bool enclosed)
    {
        var (vertices, indices) = Procedural.UnitCube();
        var mesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, Material(pbr, Vector3.Zero))]);
        var dark = pbr.Materials.AddDefaultMaterial(new Vector4(0, 0, 0, 1));
        var wall = new PbrMesh([mesh.Primitives[0] with { MaterialId = dark }]);
        var scene = Scene(new Vector3(0, 2, 3), Vector3.Zero);
        scene.Ambient = new PbrAmbient { Sky = Vector3.One, Equator = Vector3.One, Ground = Vector3.One, Flat = true };
        scene.Instances.Add(new PbrInstance
        {
            Mesh = mesh, Model = Matrix4x4.CreateScale(6, 0.1f, 6) * Matrix4x4.CreateTranslation(0, -0.05f, 0),
        });
        if (enclosed)
        {
            foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            {
                foreach (var side in new[] { -1, 1 })
                    scene.GiGeometry.Instances.Add(new PbrInstance
                    {
                        Mesh = wall,
                        Model = Matrix4x4.CreateScale(new Vector3(42) - axis * 41) * Matrix4x4.CreateTranslation(axis * (20 * side)),
                    });
            }
        }
        pbr.Pipeline.Find<ProbeGiFeature>()!.Settings = new PbrGi
        {
            Enabled = true, RaysPerProbe = 128, Hysteresis = 0.5f,
            Volume = new PbrProbeVolume(new Vector3(-2, 0.25f, -2), Vector3.One, 5, 3, 5),
        };
        return scene;
    }

    private static PbrScene EmissiveScene(PbrRenderer pbr, bool enabled)
    {
        var (vertices, indices) = Procedural.UnitCube();
        var white = new PbrMesh([pbr.UploadPrimitive(vertices, indices, Material(pbr, Vector3.Zero))]);
        var glow = new PbrMesh([pbr.UploadPrimitive(vertices, indices, Material(pbr, new Vector3(6, 0, 0)))]);
        var scene = Scene(new Vector3(0, 1, 0.5f), new Vector3(0, 1, -2));
        scene.Instances.Add(new PbrInstance
        {
            Mesh = white, Model = Matrix4x4.CreateScale(6, 4, 0.2f) * Matrix4x4.CreateTranslation(0, 1, -2),
        });
        scene.GiGeometry.IncludeSceneInstances = false;
        scene.GiGeometry.Instances.Add(scene.Instances[0]);
        scene.GiGeometry.Instances.Add(new PbrInstance
        {
            Mesh = glow, Model = Matrix4x4.CreateScale(6, 4, 0.2f) * Matrix4x4.CreateTranslation(0, 1, 2),
        });
        scene.GiGeometry.Instances.Add(new PbrInstance
        {
            Mesh = white, Model = Matrix4x4.CreateScale(6, 0.2f, 6) * Matrix4x4.CreateTranslation(0, -1, 0),
        });
        pbr.Pipeline.Find<ProbeGiFeature>()!.Settings = new PbrGi
        {
            Enabled = enabled, RaysPerProbe = 64, Hysteresis = 0.5f, MaxProbes = 512,
        };
        return scene;
    }

    private static PbrScene AoScene(PbrRenderer pbr, bool customizeGi)
    {
        var (vertices, indices) = Procedural.UnitCube();
        var mesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, Material(pbr, Vector3.Zero))]);
        var scene = Scene(new Vector3(0, 2.5f, 3.5f), Vector3.Zero);
        scene.Ambient = new PbrAmbient { Sky = Vector3.One, Equator = Vector3.One, Ground = Vector3.One, Flat = true };
        scene.RayTracedAo = new PbrRayTracedAo { Enabled = true, RaysPerPixel = 16, MaxDistance = 2 };
        scene.Instances.Add(new PbrInstance
        {
            Mesh = mesh, Model = Matrix4x4.CreateScale(6, 0.1f, 6) * Matrix4x4.CreateTranslation(0, -0.05f, 0),
        });
        scene.Instances.Add(new PbrInstance
        {
            Mesh = mesh, Model = Matrix4x4.CreateScale(6, 2, 0.2f) * Matrix4x4.CreateTranslation(0, 1, -1),
        });
        if (customizeGi)
        {
            scene.Instances[1].GiMesh = new PbrMesh([]);
            scene.GiGeometry.Instances.Add(new PbrInstance { Mesh = mesh, Model = Matrix4x4.CreateScale(20) });
        }
        return scene;
    }

    private static PbrScene Scene(Vector3 eye, Vector3 target) => new()
    {
        Camera = new PbrCamera
        {
            View = PbrMath.LookAt(eye, target, Vector3.UnitY),
            Projection = PbrMath.Perspective(MathF.PI / 3, 1, 0.1f, 100), Position = eye,
        },
        Ambient = new PbrAmbient { Sky = Vector3.Zero, Equator = Vector3.Zero, Ground = Vector3.Zero, Flat = true },
        Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Linear },
    };

    private static int Material(PbrRenderer pbr, Vector3 emissive, GltfAlphaMode alpha = GltfAlphaMode.Opaque) =>
        pbr.Materials.AddMaterial(new GltfMaterialData(
            Name: "GI geometry test", BaseColorFactor: new Vector4(0.9f, 0.9f, 0.9f, 1), MetallicFactor: 0,
            RoughnessFactor: 1, EmissiveFactor: emissive, NormalScale: 1, OcclusionStrength: 1,
            TransmissionFactor: 0, AlphaMode: alpha, AlphaCutoff: 0.5f, DoubleSided: false,
            BaseColorImage: -1, MetallicRoughnessImage: -1, NormalImage: -1, OcclusionImage: -1,
            EmissiveImage: -1, BaseColorUvTransform: GltfUvTransform.Identity), []);

    private static byte[] Render(WebGpuRenderer backend, PbrRenderer pbr, PbrScene scene, int frames)
    {
        for (var i = 0; i < frames; i++) pbr.RenderFrame(scene);
        return (byte[])backend.ReadbackColor(out _, out _).Clone();
    }

    private static double Red(byte[] pixels)
    {
        long red = 0;
        for (var i = 2; i < pixels.Length; i += 4) red += pixels[i];
        return red / (pixels.Length / 4.0);
    }
}
