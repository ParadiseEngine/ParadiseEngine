using BenchmarkDotNet.Attributes;
using Paradise.ECS;

namespace Paradise.ECS.Benchmarks;

/// <summary>
/// End-to-end benchmark comparing wave scheduler strategies:
/// Sequential, Parallel.For, JobWorkerPool, and WorkStealingPool.
/// </summary>
[MemoryDiagnoser]
public class WaveSchedulerBenchmarks
{
    [Params(10, 100, 1000)]
    public int EntityCount { get; set; }

    private SharedWorld _sharedWorld = null!;
    private World _world = null!;
    private SystemSchedule<ComponentMask, DefaultConfig> _seqSchedule = null!;
    private SystemSchedule<ComponentMask, DefaultConfig> _parSchedule = null!;
    private SystemSchedule<ComponentMask, DefaultConfig> _jobSchedule = null!;
    private SystemSchedule<ComponentMask, DefaultConfig> _wsSchedule = null!;
    private JobWorkerPool _jobPool = null!;
    private WorkStealingPool _wsPool = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sharedWorld = SharedWorldFactory.Create();
        _world = _sharedWorld.CreateWorld();

        for (int i = 0; i < EntityCount; i++)
        {
            var e = _world.Spawn();
            _world.AddComponent(e, new BenchPosition { X = i, Y = i * 2, Z = 0 });
            _world.AddComponent(e, new BenchVelocity { X = 1, Y = 1, Z = 0 });
        }

        _seqSchedule = SystemSchedule.Create(_world)
            .Add<BenchMovementSystem>()
            .Build<SequentialWaveScheduler>();

        _parSchedule = SystemSchedule.Create(_world)
            .Add<BenchMovementSystem>()
            .Build<ParallelWaveScheduler>();

        _jobPool = new JobWorkerPool();
        _jobSchedule = SystemSchedule.Create(_world)
            .Add<BenchMovementSystem>()
            .Build(new JobWaveScheduler(_jobPool));

        _wsPool = new WorkStealingPool();
        _wsSchedule = SystemSchedule.Create(_world)
            .Add<BenchMovementSystem>()
            .Build(new WorkStealingWaveScheduler(_wsPool));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _seqSchedule.Dispose();
        _parSchedule.Dispose();
        _jobSchedule.Dispose();
        _wsSchedule.Dispose();
        _jobPool.Dispose();
        _wsPool.Dispose();
        _sharedWorld.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Sequential() => _seqSchedule.Run();

    [Benchmark]
    public void ParallelFor() => _parSchedule.Run();

    [Benchmark]
    public void JobPool() => _jobSchedule.Run();

    [Benchmark]
    public void WorkStealing() => _wsSchedule.Run();
}
