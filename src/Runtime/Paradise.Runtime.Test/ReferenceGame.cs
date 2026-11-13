using System.Numerics;
using Paradise.Hosting;
using Paradise.Rendering;
using Paradise.Rendering.Pbr;
using Paradise.Runtime.Pbr;
using Paradise.Windowing;
using Zio;

namespace Paradise.Runtime.Test;

/// <summary>Where a renderable sits: a row-vector model matrix.</summary>
[Component]
public partial struct Placement
{
    public Matrix4x4 Model;
}

/// <summary>Turns about Y at a fixed position.</summary>
[Component]
public partial struct Spin
{
    public Vector3 Position;
    public float Angle;
    public float RadiansPerTick;
}

/// <summary>Drawn with the game's cube; <see cref="Key"/> keeps its render instance across frames.</summary>
[Component]
public partial struct Renderable
{
    public long Key;
}

[Queryable]
[With<Spin>]
[With<Placement>]
public readonly ref partial struct Spinning;

[Queryable]
[With<Placement>(IsReadOnly = true)]
[With<Renderable>(IsReadOnly = true)]
public readonly ref partial struct Visible;

/// <summary>The reference game: what a downstream game's composition looks like, small enough to read.</summary>
/// <remarks>
/// Simulation (its own thread under <see cref="ParadiseHost"/>, the caller's under <see cref="CooperativeHost"/>)
/// spins entities in an ECS world and publishes world copies through a <see cref="SnapshotStream{T}"/>.
/// Presentation loads the cooked cube through an <see cref="AssetLoader"/>, extracts each snapshot into a
/// <see cref="PbrScene"/> with a <see cref="PbrSceneBuilder"/> and renders it with <see cref="PbrRenderer"/>.
/// </remarks>
internal sealed class ReferenceGame(
    IFileSystem content, Func<SurfaceDescriptor, IRenderer> backend, int entities = 9, Action<IRenderer>? afterCompleteFrame = null)
    : IHostApplication, IDisposable
{
    public const string CubePath = "/meshes/cube.mesh";

    private readonly SharedWorld _shared = SharedWorldFactory.Create();

    public SnapshotStream<World> Snapshots { get; } = new();
    public IFileSystem Content => content;
    public Action<IRenderer>? AfterCompleteFrame => afterCompleteFrame;
    public IRenderer? Backend { get; private set; }
    public int Entities => entities;

    /// <summary>Frames that drew every entity, after the cube finished loading.</summary>
    public int CompleteFrames { get; private set; }
    public int LastInstanceCount { get; private set; }
    public IReadOnlyList<PbrInstance> LastInstances { get; private set; } = [];
    public IReadOnlyList<Matrix4x4> LastSnapshotModels { get; private set; } = [];

    /// <summary>Every instance the scene has held in a complete frame; stable identity keeps this at one per entity.</summary>
    public HashSet<PbrInstance> SeenInstances { get; } = [];

    public IHostSimulation CreateSimulation(HostContext context) => new Simulation(this, context);

    public IHostPresentation CreatePresentation(HostContext context, in SurfaceDescriptor surface)
    {
        Backend = backend(surface);
        return new Presentation(this, context, Backend);
    }

    public void Dispose() => _shared.Dispose();

    private sealed class Simulation : IHostSimulation
    {
        private readonly ReferenceGame _game;
        private readonly HostContext _context;
        private readonly World _live;
        private long _frame;

        public Simulation(ReferenceGame game, HostContext context)
        {
            _game = game;
            _context = context;
            _live = game._shared.CreateWorld();
            for (var i = 0; i < game.Entities; i++)
            {
                var entity = _live.Spawn();
                var position = new Vector3((i % 3 - 1) * 1.6f, (i / 3 - 1) * 1.6f, 0f);
                _live.AddComponent(entity, new Spin { Position = position, Angle = i * 0.3f, RadiansPerTick = 0.05f });
                _live.AddComponent(entity, new Placement { Model = Matrix4x4.CreateTranslation(position) });
                _live.AddComponent(entity, new Renderable { Key = i });
            }

            // Presentation can draw before the first tick.
            Publish();
        }

        public TimeSpan FixedStep => TimeSpan.FromSeconds(1.0 / 60.0);

        public void HandleInput(in TimedWindowEvent input) { }

        public void Tick()
        {
            foreach (var row in Spinning.Query<World, ComponentMask, DefaultConfig>(_live))
            {
                ref var spin = ref row.Spin;
                spin.Angle += spin.RadiansPerTick;
                row.Placement.Model = Matrix4x4.CreateRotationY(spin.Angle) * Matrix4x4.CreateTranslation(spin.Position);
            }
        }

        public void AfterTicks() => Publish();

        private void Publish()
        {
            var snapshot = _game.Snapshots.TryTakeRecycled(out var recycled) ? recycled : _game._shared.CreateWorld();
            snapshot.CopyFrom(_live);
            _game.Snapshots.Publish(snapshot, _frame++, _context.Clock.Elapsed);
        }

        public void Dispose() { }
    }

    private sealed class Presentation : IHostPresentation
    {
        private readonly ReferenceGame _game;
        private readonly IRenderer _backend;
        private readonly PbrRenderer _renderer;
        private readonly AssetLoader _assets;
        private readonly AssetHandle<PbrMeshAsset> _cube;
        private readonly PbrSceneBuilder _scene = new();
        private readonly List<Matrix4x4> _models = [];

        public Presentation(ReferenceGame game, HostContext context, IRenderer backend)
        {
            _game = game;
            _backend = backend;
            _renderer = new PbrRenderer(backend, context.Switches, context.Width, context.Height);
            _assets = new AssetLoader(game.Content);
            _cube = _assets.Load(CubePath, new PbrMeshAssetType(_renderer));

            var eye = new Vector3(0f, 1.5f, 6f);
            _scene.Scene.Camera = new PbrCamera
            {
                View = PbrMath.LookAt(eye, Vector3.Zero, Vector3.UnitY),
                Projection = PbrMath.Perspective(MathF.PI / 3f, (float)context.Width / context.Height, 0.1f, 50f),
                Position = eye,
            };
            _scene.Scene.Lights.Add(new PbrLight
            {
                Type = PbrLightType.Directional,
                Direction = Vector3.Normalize(new Vector3(0.4f, 1f, 0.6f)),
                Intensity = 3f,
            });
        }

        public void Resize(uint width, uint height) => _renderer.Resize(width, height);

        public void Render(TimeSpan elapsed, TimeSpan delta)
        {
            _assets.Update();
            if (!_game.Snapshots.TryRead(out var snapshot)) return;
            _scene.Begin();
            _models.Clear();
            try
            {
                foreach (var row in Visible.Query<World, ComponentMask, DefaultConfig>(snapshot.World))
                {
                    _models.Add(row.Placement.Model);
                    _scene.Add(row.Renderable.Key, _cube, row.Placement.Model);
                }
            }
            finally
            {
                _game.Snapshots.Recycle(snapshot.World);
            }

            _scene.End();
            _renderer.RenderFrame(_scene.Scene);
            _game.LastInstanceCount = _scene.Scene.Instances.Count;
            _game.LastInstances = [.. _scene.Scene.Instances];
            _game.LastSnapshotModels = [.. _models];
            if (_scene.Skipped != 0 || _scene.Scene.Instances.Count != _game.Entities) return;
            _game.CompleteFrames++;
            _game.SeenInstances.UnionWith(_scene.Scene.Instances);
            _game.AfterCompleteFrame?.Invoke(_backend);
        }

        public Task CaptureAsync(string path) => Task.CompletedTask;

        public void Dispose()
        {
            _assets.Release(_cube);
            _assets.Update();
            _assets.Dispose();
            _renderer.Dispose();
            if (_backend is IDisposable disposable) disposable.Dispose();
        }
    }
}
