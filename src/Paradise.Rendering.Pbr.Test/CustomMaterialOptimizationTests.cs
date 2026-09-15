using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Paradise.Assets.Gltf;
using Paradise.Rendering.Pbr.Test.Baseline;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

public class CustomMaterialOptimizationTests
{
    private const uint Size = 96;

    private sealed class ErrorLog : ILogger
    {
        public ConcurrentQueue<string> Errors { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) Errors.Enqueue(formatter(state, exception));
        }
    }

    private static WebGpuRenderer? Backend(ILogger? logger = null)
    {
        try { return WebGpuRenderer.CreateHeadless(Size, Size, logger); }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test($"No WebGPU adapter: {error.Message}");
            return null;
        }
    }

    private static GltfMaterialData Material() => new("custom", new Vector4(0.2f, 0.4f, 0.7f, 1),
        0f, 0.8f, Vector3.Zero, 1f, 1f, 0f, GltfAlphaMode.Opaque, 0.5f, false,
        -1, -1, -1, -1, -1, GltfUvTransform.Identity);

    private static PbrScene Scene()
    {
        var eye = new Vector3(0, 2, 7);
        return new PbrScene
        {
            Camera = new PbrCamera
            {
                View = PbrMath.LookAt(eye, Vector3.Zero, Vector3.UnitY), Position = eye,
                Projection = PbrMath.Perspective(1f, 1f, 0.1f, 30f),
            },
            Ambient = new PbrAmbient { Sky = new Vector3(0.5f), Flat = true },
        };
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task custom_instancing_preserves_fragment_instance_data_and_material_bindings(bool reorder)
    {
        var errors = new ErrorLog();
        using var backend = Backend(errors);
        if (backend is null) return;
        backend.NativeDevice.PushErrorScope(WebGpuSharp.ErrorFilter.Validation);
        var recording = new RecordingRenderer(backend);
        using var pbr = new PbrRenderer(recording, new FeatureSwitches(), Size, Size);
        var program = pbr.RegisterMaterialProgram(ShaderProgramLoader.Load(
            typeof(CustomMaterialOptimizationTests).Assembly, "Shaders.instancedMaterialFixture"), options: new()
            {
                PreservesMeshBounds = true,
                AllowsOpaqueReordering = true,
                InstancedVertexEntryPoint = "vertexMainInstanced",
                InstancedFragmentEntryPoint = "fragmentMainInstanced",
            });
        var tintA = backend.CreateBufferWithData<Vector4>(new BufferDesc("TintA", 0, BufferUsage.Uniform), [new(0.6f, 0, 0, 0)]);
        var tintB = backend.CreateBufferWithData<Vector4>(new BufferDesc("TintB", 0, BufferUsage.Uniform), [new(0, 0.5f, 0, 0)]);
        try
        {
            var first = pbr.Materials.AddMaterial(Material(), [], program, [BindGroupEntryDesc.ForBuffer(7, tintA, 0, 16)]);
            var second = pbr.Materials.AddMaterial(Material(), [], program, [BindGroupEntryDesc.ForBuffer(7, tintB, 0, 16)]);
            var (vertices, indices) = Procedural.UnitCube();
            var primitive = pbr.UploadPrimitive(vertices, indices, first);
            var meshes = new[] { new PbrMesh([primitive]), new PbrMesh([primitive with { MaterialId = second }]) };
            var scene = Scene();
            // A distinct first draw ensures custom SV_InstanceID values do not start at zero.
            scene.Instances.Add(new PbrInstance
            {
                Mesh = new PbrMesh([primitive with { MaterialId = pbr.Materials.AddDefaultMaterial(new Vector4(0.1f, 0.9f, 0.1f, 1)) }]),
                Model = Matrix4x4.CreateScale(0.3f) * Matrix4x4.CreateTranslation(0, 1.5f, 0),
            });
            for (var i = 0; i < 6; i++)
                scene.Instances.Add(new PbrInstance
                {
                    Mesh = meshes[reorder ? i % 2 : i / 3],
                    Model = Matrix4x4.CreateScale(0.6f, 0.8f + i * 0.03f, 0.5f)
                        * Matrix4x4.CreateTranslation((i % 3 - 1) * 1.4f, 0, i / 3 * -1.5f),
                    Highlight = i * 0.08f,
                    GiMode = i % 2 == 0 ? PbrGiMode.Disabled : PbrGiMode.Dynamic,
                });
            scene.Instancing = new PbrInstancing { ReorderOpaque = reorder };
            pbr.RenderFrame(scene);
            var batched = backend.ReadbackColor(out _, out _).ToArray();
            var scope = backend.NativeDevice.PopErrorScopeSync(5_000_000_000UL);
            await Assert.That(scope.message).IsEqualTo(string.Empty);
            await Assert.That(string.Join("\n", errors.Errors)).IsEqualTo(string.Empty);
            var instancing = pbr.Pipeline.Find<InstancingFeature>()!;
            await Assert.That(instancing.DrawCalls).IsEqualTo(3);
            await Assert.That(instancing.SavedDrawCalls).IsEqualTo(4);
            await Assert.That(pbr.Materials.IsOccluder(first)).IsFalse();
            await Assert.That(recording.LastPresentedFrame.Commands.Any(command => command.Kind == RenderCommandKind.DrawIndexed
                && command.DrawIndexed.InstanceCount == 3 && command.DrawIndexed.FirstInstance > 0)).IsTrue();

            scene.Instancing = new PbrInstancing { Enabled = false };
            pbr.RenderFrame(scene);
            await Assert.That(instancing.DrawCalls).IsEqualTo(7);
            await Assert.That(batched.AsSpan().SequenceEqual(backend.ReadbackColor(out _, out _))).IsTrue();
            await Assert.That(batched.Where((_, index) => index % 4 != 3).Count(value => value > 20)).IsGreaterThan(100);
        }
        finally
        {
            backend.DestroyBuffer(tintA);
            backend.DestroyBuffer(tintB);
        }
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(true, false, false)]
    [Arguments(false, true, false)]
    [Arguments(false, false, true)]
    public async Task custom_bounds_reordering_and_coverage_are_independent(bool bounds, bool reorder, bool coverage)
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var program = pbr.RegisterMaterialProgram(ShaderProgramLoader.Load(
            typeof(CustomMaterialOptimizationTests).Assembly, "Shaders.surfaceFixture"), options: new()
            {
                PreservesMeshBounds = bounds,
                AllowsOpaqueReordering = reorder,
                OpaqueCoverage = coverage,
            });
        var material = pbr.Materials.AddMaterial(Material(), [], program);
        var (vertices, indices) = Procedural.UnitCube();
        var mesh = new PbrMesh([pbr.UploadPrimitive(vertices, indices, material)]);
        var scene = Scene();
        scene.Instances.Add(new PbrInstance { Mesh = mesh });
        scene.Instances.Add(new PbrInstance { Mesh = mesh, Model = Matrix4x4.CreateTranslation(100, 0, 0) });
        pbr.RenderFrame(scene);
        var pixels = backend.ReadbackColor(out _, out _).ToArray();
        await Assert.That(pbr.Pipeline.Find<FrustumCullingFeature>()!.CulledDrawCount).IsEqualTo(bounds ? 1 : 0);
        await Assert.That(pbr.Materials.AllowsOpaqueReordering(material)).IsEqualTo(reorder);
        await Assert.That(pbr.Materials.IsOccluder(material)).IsEqualTo(coverage);
        await Assert.That(pbr.Materials.SupportsInstancing(material)).IsFalse();
        scene.Visibility.FrustumEnabled = false;
        pbr.RenderFrame(scene);
        await Assert.That(pixels.AsSpan().SequenceEqual(backend.ReadbackColor(out _, out _))).IsTrue();
    }

    [Test]
    public async Task invalid_instanced_entries_fail_before_program_registration()
    {
        using var backend = Backend();
        if (backend is null) return;
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), Size, Size);
        var fixture = ShaderProgramLoader.Load(typeof(CustomMaterialOptimizationTests).Assembly, "Shaders.instancedMaterialFixture");
        await Assert.That(() => pbr.RegisterMaterialProgram(fixture, options: new()
        {
            InstancedVertexEntryPoint = "missing",
        })).Throws<InvalidOperationException>();
        await Assert.That(() => pbr.RegisterMaterialProgram(fixture, options: new()
        {
            InstancedFragmentEntryPoint = "fragmentMainInstanced",
        })).Throws<ArgumentException>();
        await Assert.That(() => pbr.RegisterMaterialProgram(fixture, options: new()
        {
            InstancedVertexEntryPoint = "vertexMainInstanced",
            InstancedFragmentEntryPoint = "missing",
        })).Throws<InvalidOperationException>();
        var noStorage = fixture with
        {
            Layout = fixture.Layout with
            {
                Groups = fixture.Layout.Groups.Select(group => group.GroupIndex == 0
                    ? group with { Entries = group.Entries.Where(entry => entry.Binding != 1).ToArray() } : group).ToArray(),
            },
        };
        await Assert.That(() => pbr.RegisterMaterialProgram(noStorage, options: new()
        {
            InstancedVertexEntryPoint = "vertexMainInstanced",
        })).Throws<InvalidOperationException>();
        await Assert.That(pbr.CustomProgramCountForTest).IsEqualTo(0);
    }
}
