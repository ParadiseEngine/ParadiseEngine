using System.Numerics;
using Paradise.Hosting;
using Paradise.Rendering;
using Paradise.Rendering.Pbr;
using Paradise.Runtime.Pbr;
using Paradise.Windowing;
using Zio;

namespace Paradise.Runtime.Benchmarks;

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

/// <summary>The reference game's composition, instrumented: simulation spins entities in an ECS world and
/// publishes world copies; presentation loads the cooked cube, extracts each snapshot into a
/// <see cref="PbrScene"/> and renders it.</summary>
/// <remarks>Unlike the test's <c>ReferenceGame</c> it keeps no per-frame observations, so every byte it
/// reports is the engine's or the composition's a real game would also pay.</remarks>
internal sealed class BenchmarkGame(BenchmarkOptions options, IFileSystem content, AllocationLedger ledger)
    : IHostApplication, IDisposable
{
    public const string CubePath = "/meshes/cube.mesh";

    private readonly SharedWorld _shared = SharedWorldFactory.Create();
    private readonly SnapshotStream<World> _snapshots = new();
    private readonly AllocationLedger.Phase _churn = ledger.Get("sim: churn (despawn + spawn)");
    private readonly AllocationLedger.Phase _tick = ledger.Get("sim: tick (ECS query)");
    private readonly AllocationLedger.Phase _publish = ledger.Get("sim: publish (snapshot copy)");
    private readonly AllocationLedger.Phase _assets = ledger.Get("present: AssetLoader.Update");
    private readonly AllocationLedger.Phase _extract = ledger.Get("present: extract (query + PbrSceneBuilder)");
    private readonly AllocationLedger.Phase _animate = ledger.Get("present: animate scene settings");
    private readonly AllocationLedger.Phase _render = ledger.Get("present: PbrRenderer.RenderFrame (total)");

    public BenchmarkOptions Options { get; } = options;
    public IFileSystem Content { get; } = content;
    public AllocationLedger Ledger { get; } = ledger;

    /// <summary>Raised on the render thread after each frame that drew every renderable.</summary>
    public event Action<int>? CompleteFrame;

    public IRenderer? Backend { get; private set; }
    public IWindow? Window { get; private set; }
    public PbrRenderer? Renderer { get; private set; }
    public int CompleteFrames { get; private set; }
    public int LastInstanceCount { get; private set; }

    public IHostSimulation CreateSimulation(HostContext context) => new Simulation(this, context);

    public IHostPresentation CreatePresentation(HostContext context, in SurfaceDescriptor surface)
    {
        Window = context.Window;
        Backend = Options.Backend == BenchmarkBackend.WebGpu
            ? new Paradise.Rendering.WebGPU.WebGpuRenderer(in surface)
            : new NullRenderer();
        return new Presentation(this, context, Backend);
    }

    public void Dispose() => _shared.Dispose();

    private sealed class Simulation : IHostSimulation
    {
        private readonly BenchmarkGame _game;
        private readonly HostContext _context;
        private readonly World _live;
        private readonly Entity[] _entities;
        private readonly int _side;
        private int _oldest;
        private long _frame;

        public Simulation(BenchmarkGame game, HostContext context)
        {
            _game = game;
            _context = context;
            _live = game._shared.CreateWorld();
            var count = game.Options.Entities;
            _entities = new Entity[count];
            _side = (int)MathF.Ceiling(MathF.Sqrt(count));
            for (var i = 0; i < count; i++) _entities[i] = SpawnAt(i);

            Publish();
        }

        /// <summary>Spawns the entity for a grid slot, keyed by its full identity, so a replacement in the
        /// same slot is a new renderable, as it is to motion history.</summary>
        private Entity SpawnAt(int slot)
        {
            var entity = _live.Spawn();
            var position = new Vector3((slot % _side - (_side - 1) * 0.5f) * 1.6f, 0.8f + slot / _side * 0.4f, -(slot / _side) * 1.6f);
            _live.AddComponent(entity, new Spin { Position = position, Angle = slot * 0.3f, RadiansPerTick = 0.05f });
            _live.AddComponent(entity, new Placement { Model = Matrix4x4.CreateTranslation(position) });
            _live.AddComponent(entity, new Renderable { Key = ((long)entity.Version << 32) | (uint)entity.Id });
            return entity;
        }

        /// <summary>Lockstep runs make this equal to the host's catch-up budget, so each frame owes exactly one tick.</summary>
        public TimeSpan FixedStep => _game.Options.FixedStep;

        public void HandleInput(in TimedWindowEvent input) { }

        public void Tick()
        {
            var churn = _game.Options.Churn;
            if (churn > 0)
            {
                var started = AllocationLedger.Now();
                for (var i = 0; i < churn; i++)
                {
                    _live.Despawn(_entities[_oldest]);
                    _entities[_oldest] = SpawnAt(_oldest);
                    _oldest = (_oldest + 1) % _entities.Length;
                }

                _game._churn.Add(started);
            }

            var start = AllocationLedger.Now();
            foreach (var row in Spinning.Query<World, ComponentMask, DefaultConfig>(_live))
            {
                ref var spin = ref row.Spin;
                spin.Angle += spin.RadiansPerTick;
                row.Placement.Model = Matrix4x4.CreateRotationY(spin.Angle) * Matrix4x4.CreateTranslation(spin.Position);
            }

            _game._tick.Add(start);
        }

        public void AfterTicks()
        {
            var start = AllocationLedger.Now();
            Publish();
            _game._publish.Add(start);
        }

        private void Publish()
        {
            var snapshot = _game._snapshots.TryTakeRecycled(out var recycled) ? recycled : _game._shared.CreateWorld();
            snapshot.CopyFrom(_live);
            _game._snapshots.Publish(snapshot, _frame++, _context.Clock.Elapsed);
        }

        public void Dispose() { }
    }

    private sealed class Presentation : IHostPresentation
    {
        private const long PropKeys = 1L << 40;

        private readonly BenchmarkGame _game;
        private readonly IRenderer _backend;
        private readonly PbrRenderer _renderer;
        private readonly RendererProbes? _probes;
        private readonly AssetLoader _assets;
        private readonly AssetHandle<PbrMeshAsset> _cube;
        private readonly PbrSceneBuilder _scene = new();
        private readonly ShowcaseScene? _showcase;
        private readonly int _expected;
        private int _frame;

        public Presentation(BenchmarkGame game, HostContext context, IRenderer backend)
        {
            _game = game;
            _backend = backend;
            _renderer = new PbrRenderer(backend, context.Switches, context.Width, context.Height);
            game.Renderer = _renderer;
            if (game.Options.ProbeFeatures) _probes = RendererProbes.Install(_renderer, game.Ledger);
            _assets = new AssetLoader(game.Content);
            _cube = _assets.Load(CubePath, new PbrMeshAssetType(_renderer));

            var eye = new Vector3(0f, 3f, 9f);
            _scene.Scene.Camera = new PbrCamera
            {
                View = PbrMath.LookAt(eye, new Vector3(0f, 0.5f, -2f), Vector3.UnitY),
                Projection = PbrMath.Perspective(MathF.PI / 3f, (float)context.Width / context.Height, 0.1f, 80f),
                Position = eye,
            };
            if (game.Options.Workload == BenchmarkWorkload.Showcase)
            {
                _showcase = new ShowcaseScene(_renderer, _scene.Scene);
            }
            else
            {
                _scene.Scene.Lights.Add(new PbrLight
                {
                    Type = PbrLightType.Directional,
                    Direction = Vector3.Normalize(new Vector3(0.4f, 1f, 0.6f)),
                    Intensity = 3f,
                });
            }

            _expected = game.Options.Entities + (_showcase?.Props.Count ?? 0);
        }

        public void Resize(uint width, uint height) => _renderer.Resize(width, height);

        public void Render(TimeSpan elapsed, TimeSpan delta)
        {
            var start = AllocationLedger.Now();
            _assets.Update();
            _game._assets.Add(start);

            start = AllocationLedger.Now();
            if (!_game._snapshots.TryRead(out var snapshot)) return;
            _scene.Begin();
            try
            {
                foreach (var row in Visible.Query<World, ComponentMask, DefaultConfig>(snapshot.World))
                {
                    var instance = _scene.Add(row.Renderable.Key, _cube, row.Placement.Model);
                    if (instance is not null) instance.GiMode = PbrGiMode.Dynamic;
                }

                if (_showcase is not null)
                {
                    var props = _showcase.Props;
                    for (var i = 0; i < props.Count; i++)
                    {
                        var instance = _scene.Add(PropKeys + i, props[i].Mesh, props[i].Model);
                        instance.GiMode = props[i].GiMode;
                    }
                }
            }
            finally
            {
                _game._snapshots.Recycle(snapshot.World);
            }

            _scene.End();
            _game._extract.Add(start);

            start = AllocationLedger.Now();
            _scene.Scene.ElapsedSeconds = _frame / 60f;
            _showcase?.Animate(_frame, _game.Options.RecordUpdates);
            _game._animate.Add(start);

            _probes?.BeginFrame();
            start = AllocationLedger.Now();
            _renderer.RenderFrame(_scene.Scene);
            _game._render.Add(start);
            _probes?.EndFrame();

            _frame++;
            _game.LastInstanceCount = _scene.Scene.Instances.Count;
            if (_scene.Skipped != 0 || _scene.Scene.Instances.Count != _expected) return;
            _game.CompleteFrames++;
            _game.CompleteFrame?.Invoke(_game.CompleteFrames);
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
