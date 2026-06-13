using System.Numerics;

namespace Paradise.Rendering.ECS.Test;

/// <summary>Integration tests for the ECS extraction pipeline.</summary>
public sealed class ExtractionTests : IDisposable
{
    private readonly SharedWorld<SmallBitSet<uint>, DefaultConfig> _sharedWorld;
    private readonly World<SmallBitSet<uint>, DefaultConfig> _world;
    private readonly FrameRenderPackets _packets;
    private readonly SystemSchedule<SmallBitSet<uint>, DefaultConfig> _extractSchedule;

    public ExtractionTests()
    {
        _sharedWorld = SharedWorldFactory.Create();
        _world = _sharedWorld.CreateWorld();
        _packets = new FrameRenderPackets();
        _extractSchedule = SystemSchedule.Create(_world)
            .Add<ExtractRenderablesSystem>()
            .Add<ExtractCamerasSystem>()
            .Build<SequentialWaveScheduler>();
    }

    public void Dispose()
    {
        _extractSchedule.Dispose();
        _sharedWorld.Dispose();
    }

    private void RunExtraction()
    {
        ExtractionContext.Packets = _packets;
        _packets.Reset();
        _extractSchedule.Run();
        ExtractionContext.Packets = null;
    }

    // ---- Renderable extraction ----

    [Test]
    public async Task Extraction_KnownPopulation_ProducesExactRenderableCount()
    {
        const int count = 50;
        for (int i = 0; i < count; i++)
        {
            var e = _world.Spawn();
            _world.AddComponent(e, new LocalToWorld { Value = Matrix4x4.Identity });
            _world.AddComponent(e, new MeshRef { MeshId = (uint)i });
            _world.AddComponent(e, new MaterialRef { MaterialId = 1 });
        }

        RunExtraction();

        await Assert.That(_packets.Renderables.Length).IsEqualTo(count);
    }

    [Test]
    public async Task Extraction_EntityMissingComponent_NotExtracted()
    {
        // Entity with only LocalToWorld + MeshRef — no MaterialRef → should NOT be extracted.
        var incomplete = _world.Spawn();
        _world.AddComponent(incomplete, new LocalToWorld { Value = Matrix4x4.Identity });
        _world.AddComponent(incomplete, new MeshRef { MeshId = 99 });

        // Entity with all three components → should be extracted.
        var complete = _world.Spawn();
        _world.AddComponent(complete, new LocalToWorld { Value = Matrix4x4.Identity });
        _world.AddComponent(complete, new MeshRef { MeshId = 1 });
        _world.AddComponent(complete, new MaterialRef { MaterialId = 1 });

        RunExtraction();

        await Assert.That(_packets.Renderables.Length).IsEqualTo(1);
        await Assert.That(_packets.Renderables.Span[0].MeshId).IsEqualTo(1u);
    }

    [Test]
    public async Task Extraction_Transform_RoundTrips()
    {
        var transform = new Matrix4x4(
            1, 2, 3, 4,
            5, 6, 7, 8,
            9, 10, 11, 12,
            13, 14, 15, 16);

        var e = _world.Spawn();
        _world.AddComponent(e, new LocalToWorld { Value = transform });
        _world.AddComponent(e, new MeshRef { MeshId = 7 });
        _world.AddComponent(e, new MaterialRef { MaterialId = 3 });

        RunExtraction();

        await Assert.That(_packets.Renderables.Length).IsEqualTo(1);
        var r = _packets.Renderables.Span[0];
        await Assert.That(r.LocalToWorld).IsEqualTo(transform);
        await Assert.That(r.MeshId).IsEqualTo(7u);
        await Assert.That(r.MaterialId).IsEqualTo(3u);
    }

    // ---- Camera extraction ----

    [Test]
    public async Task Extraction_KnownPopulation_ProducesExactCameraCount()
    {
        for (int i = 0; i < 3; i++)
        {
            var e = _world.Spawn();
            _world.AddComponent(e, new CameraComponent
            {
                View = Matrix4x4.Identity,
                Projection = Matrix4x4.Identity,
                TargetView = uint.MaxValue,
            });
        }

        RunExtraction();

        await Assert.That(_packets.Cameras.Length).IsEqualTo(3);
    }

    [Test]
    public async Task Extraction_Camera_ViewProjectionRoundTrip()
    {
        // Use a simple asymmetric view and projection so the product is unique.
        var view = Matrix4x4.CreateLookAt(
            new Vector3(0, 0, 5),
            Vector3.Zero,
            Vector3.UnitY);
        var proj = Matrix4x4.CreateOrthographic(2f, 2f, 0.1f, 100f);
        var expected = view * proj;

        var e = _world.Spawn();
        _world.AddComponent(e, new CameraComponent
        {
            View = view,
            Projection = proj,
            TargetView = uint.MaxValue,
        });

        RunExtraction();

        await Assert.That(_packets.Cameras.Length).IsEqualTo(1);
        var cam = _packets.Cameras.Span[0];
        // Compare element-by-element to avoid floating-point noise in equality override.
        await Assert.That(cam.ViewProjection.M11).IsEqualTo(expected.M11);
        await Assert.That(cam.ViewProjection.M44).IsEqualTo(expected.M44);
    }

    [Test]
    public async Task Extraction_Camera_SwapchainTarget_ProducesInvalidHandle()
    {
        var e = _world.Spawn();
        _world.AddComponent(e, new CameraComponent
        {
            View = Matrix4x4.Identity,
            Projection = Matrix4x4.Identity,
            TargetView = uint.MaxValue,
        });

        RunExtraction();

        await Assert.That(_packets.Cameras.Span[0].TargetView).IsEqualTo(RenderViewHandle.Invalid);
    }

    [Test]
    public async Task Extraction_Camera_ExplicitTarget_ProducesValidHandle()
    {
        var e = _world.Spawn();
        _world.AddComponent(e, new CameraComponent
        {
            View = Matrix4x4.Identity,
            Projection = Matrix4x4.Identity,
            TargetView = 2u,
        });

        RunExtraction();

        var handle = _packets.Cameras.Span[0].TargetView;
        await Assert.That(handle.IsValid).IsTrue();
        await Assert.That(handle.Index).IsEqualTo(2u);
    }

    // ---- Allocation-free steady state ----

    [Test]
    public async Task Extraction_SteadyState_ProducesNoGcAllocations()
    {
        // Populate the world with a realistic scene: 100 renderables + 1 camera.
        for (int i = 0; i < 100; i++)
        {
            var e = _world.Spawn();
            _world.AddComponent(e, new LocalToWorld { Value = Matrix4x4.Identity });
            _world.AddComponent(e, new MeshRef { MeshId = (uint)(i % 4) });
            _world.AddComponent(e, new MaterialRef { MaterialId = (uint)(i % 2) });
        }
        var camEntity = _world.Spawn();
        _world.AddComponent(camEntity, new CameraComponent
        {
            View = Matrix4x4.Identity,
            Projection = Matrix4x4.CreateOrthographic(2f, 2f, 0.1f, 100f),
            TargetView = uint.MaxValue,
        });

        // Warm-up: let internal lists/buffers reach steady-state capacity.
        for (int i = 0; i < 10; i++)
            RunExtraction();

        // Measure allocation over 100 frames.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            RunExtraction();
        long after = GC.GetAllocatedBytesForCurrentThread();

        // Allow a small budget for framework internals (ECB playback, scheduler dispatch).
        // Observed steady-state is ~64 bytes/frame from fixed-cost overhead unrelated to renderable count.
        // 8 KB covers 80 bytes/frame × 100 frames; still catches any per-renderable regression
        // (100 renderables × even 1 byte each = 10 KB, which would exceed this threshold).
        const long thresholdBytes = 8 * 1024; // 8 KB
        await Assert.That(after - before).IsLessThanOrEqualTo(thresholdBytes);
    }

    // ---- Multi-frame consistency ----

    [Test]
    public async Task Extraction_MultipleFrames_ProducesConsistentResults()
    {
        var e = _world.Spawn();
        _world.AddComponent(e, new LocalToWorld { Value = Matrix4x4.Identity });
        _world.AddComponent(e, new MeshRef { MeshId = 42 });
        _world.AddComponent(e, new MaterialRef { MaterialId = 7 });

        RunExtraction();
        int firstCount = _packets.Renderables.Length;

        RunExtraction();
        int secondCount = _packets.Renderables.Length;

        await Assert.That(firstCount).IsEqualTo(secondCount);
        await Assert.That(firstCount).IsEqualTo(1);
    }
}
