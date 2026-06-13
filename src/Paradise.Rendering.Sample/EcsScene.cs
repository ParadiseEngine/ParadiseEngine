using System.Numerics;
using Paradise.ECS;
using Paradise.Rendering.ECS;
using Paradise.Rendering.ECS.Systems;

namespace Paradise.Rendering.Sample;

/// <summary>
/// Owns an ECS world populated with 100 renderable quad stand-ins and one camera, and drives the
/// extraction schedule each frame to populate a <see cref="FrameRenderPackets"/> buffer.
/// Demonstrates the M3 ECS-to-renderer extraction bridge.
/// </summary>
internal sealed class EcsScene : IDisposable
{
    private readonly SharedWorld<SmallBitSet<uint>, DefaultConfig> _sharedWorld;
    private readonly World<SmallBitSet<uint>, DefaultConfig> _world;
    private readonly FrameRenderPackets _packets;
    private readonly SystemSchedule<SmallBitSet<uint>, DefaultConfig> _schedule;

    public FrameRenderPackets Packets => _packets;

    public EcsScene()
    {
        _sharedWorld = SharedWorldFactory.Create();
        _world = _sharedWorld.CreateWorld();
        _packets = new FrameRenderPackets();

        // Spawn 100 renderable entities (placeholder mesh 0, material 0).
        for (int i = 0; i < 100; i++)
        {
            var e = _world.Spawn();
            _world.AddComponent(e, new LocalToWorld { Value = Matrix4x4.CreateTranslation(i, 0, 0) });
            _world.AddComponent(e, new MeshRef { MeshId = 0u });
            _world.AddComponent(e, new MaterialRef { MaterialId = 0u });
        }

        // Spawn one camera targeting the swapchain.
        var cam = _world.Spawn();
        _world.AddComponent(cam, new CameraComponent
        {
            View = Matrix4x4.CreateLookAt(new Vector3(0, 0, 50), Vector3.Zero, Vector3.UnitY),
            Projection = Matrix4x4.CreateOrthographic(100f, 75f, 0.1f, 200f),
            TargetView = uint.MaxValue,
        });

        _schedule = SystemSchedule.Create(_world)
            .Add<ExtractRenderablesSystem>()
            .Add<ExtractCamerasSystem>()
            .Build<SequentialWaveScheduler>();
    }

    /// <summary>
    /// Runs the extraction schedule for the current frame.
    /// Returns the number of renderables and cameras extracted.
    /// </summary>
    public (int Renderables, int Cameras) Extract()
    {
        ExtractionContext.Packets = _packets;
        _packets.Reset();
        _schedule.Run();
        ExtractionContext.Packets = null;
        return (_packets.Renderables.Length, _packets.Cameras.Length);
    }

    public void Dispose()
    {
        _schedule.Dispose();
        _sharedWorld.Dispose();
    }
}
