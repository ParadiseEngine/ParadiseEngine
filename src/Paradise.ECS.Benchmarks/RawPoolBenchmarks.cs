using BenchmarkDotNet.Attributes;
using Paradise.ECS;

namespace Paradise.ECS.Benchmarks;

/// <summary>
/// Direct comparison of raw pool execution strategies.
/// </summary>
[MemoryDiagnoser]
public class RawPoolBenchmarks
{
    [Params(10, 100, 1000)]
    public int ItemCount { get; set; }

    private JobWorkerPool _jobPool = null!;
    private WorkStealingPool _wsPool = null!;
    private double[] _results = null!;
    private List<SimWorkItem> _items = null!;

    [GlobalSetup]
    public void Setup()
    {
        _jobPool = new JobWorkerPool();
        _wsPool = new WorkStealingPool();
        _results = new double[ItemCount];
        _items = new List<SimWorkItem>(ItemCount);
        for (int i = 0; i < ItemCount; i++)
            _items.Add(new SimWorkItem(i, _results));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _jobPool.Dispose();
        _wsPool.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Sequential()
    {
        for (int i = 0; i < ItemCount; i++)
            SimWorkItem.SimulateWork(i, _results);
    }

    [Benchmark]
    public void ParallelFor()
    {
        Parallel.For(0, ItemCount, i => SimWorkItem.SimulateWork(i, _results));
    }

    [Benchmark]
    public void JobPool()
    {
        _jobPool.ExecuteWork(_items);
    }

    [Benchmark]
    public void WorkStealing()
    {
        _wsPool.ExecuteWork(_items);
    }

    internal readonly struct SimWorkItem(int index, double[] results) : IWorkItem
    {
        public void Invoke() => SimulateWork(index, results);

        public static void SimulateWork(int index, double[] results)
        {
            // ~100 iterations of trig to simulate meaningful per-item work
            double sum = 0;
            for (int j = 0; j < 100; j++)
                sum += Math.Sin(index + j);
            results[index] = sum;
        }
    }
}
