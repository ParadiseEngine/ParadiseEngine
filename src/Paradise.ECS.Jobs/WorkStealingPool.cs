using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Paradise.ECS;

/// <summary>
/// Work-stealing thread pool for parallel job execution.
/// Each worker has a per-worker Chase-Lev deque. Work is distributed round-robin
/// at dispatch time. Workers pop from their own deque (LIFO for cache locality),
/// then steal from neighbors when empty (FIFO for fairness).
/// The main (calling) thread participates as lane 0 for N+1 total parallelism.
/// </summary>
public sealed class WorkStealingPool : IDisposable
{
    private const int StateIdle = 0;
    private const int StateRunning = 1;
    private const int StateDisposing = 2;
    private const int StateDisposed = 3;

    private readonly Thread[] _workers;
    private readonly int _workerCount;
    private readonly WorkStealingDeque[] _deques;
    private readonly int _totalLanes; // workerCount + 1 (main thread = lane 0)

    // Per-wave state
    private Action<int>? _invoker;
    private int _remainingItems;
    private ConcurrentQueue<ExceptionDispatchInfo>? _exceptions;

    // Synchronization
    private readonly ManualResetEventSlim _workAvailable = new(false);
    private readonly Barrier _waveBarrier;
    private volatile bool _shutdown;
    private int _state;

    // Cached adapter for zero-allocation item dispatch
    private object? _cachedAdapter;

    /// <summary>
    /// Gets the number of worker threads (excludes the main thread).
    /// </summary>
    public int WorkerCount => _workerCount;

    /// <summary>
    /// Initializes a new <see cref="WorkStealingPool"/> with the specified number of worker threads.
    /// </summary>
    /// <param name="workerCount">
    /// Number of background worker threads. Defaults to <c>Environment.ProcessorCount - 1</c> (minimum 1).
    /// The calling thread also participates in work, so total parallelism is <paramref name="workerCount"/> + 1.
    /// </param>
    public WorkStealingPool(int workerCount = -1)
    {
        _workerCount = workerCount < 0
            ? Math.Max(1, Environment.ProcessorCount - 1)
            : Math.Max(1, workerCount);

        _totalLanes = _workerCount + 1;
        // Post-phase action resets _workAvailable BEFORE participants are released,
        // preventing workers from re-entering ProcessLane between waves.
        _waveBarrier = new Barrier(_totalLanes, _ => _workAvailable.Reset());
        _deques = new WorkStealingDeque[_totalLanes];
        for (int i = 0; i < _totalLanes; i++)
            _deques[i] = new WorkStealingDeque();

        _workers = new Thread[_workerCount];
        for (int i = 0; i < _workerCount; i++)
        {
            int laneIndex = i + 1; // lane 0 = main thread
            var worker = new Thread(() => WorkerLoop(laneIndex))
            {
                Name = $"Paradise.ECS Stealer {i}",
                IsBackground = true,
            };
            _workers[i] = worker;
            worker.Start();
        }
    }

    /// <summary>
    /// Distributes work items across all lanes using round-robin,
    /// then processes them with work-stealing. Blocks until all items are processed.
    /// </summary>
    /// <typeparam name="T">The work item type implementing <see cref="IWorkItem"/>.</typeparam>
    /// <param name="items">The work items to process.</param>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The pool is already executing work (reentrant call).</exception>
    /// <exception cref="AggregateException">One or more work items threw exceptions.</exception>
    public void ExecuteWork<T>(IReadOnlyList<T> items) where T : IWorkItem
    {
        // State machine: Idle → Running
        var previousState = Interlocked.CompareExchange(ref _state, StateRunning, StateIdle);
        switch (previousState)
        {
            case StateRunning:
                throw new InvalidOperationException("WorkStealingPool.ExecuteWork is not reentrant. A concurrent call is already in progress.");
            case StateDisposing:
            case StateDisposed:
                throw new ObjectDisposedException(nameof(WorkStealingPool));
        }

        try
        {
            switch (items.Count)
            {
                case <= 0:
                    return;
                case 1:
                    items[0].Invoke();
                    return;
            }

            var adapter = _cachedAdapter as ItemsAdapter<T>;
            if (adapter == null)
            {
                adapter = new ItemsAdapter<T>();
                _cachedAdapter = adapter;
            }

            adapter.Items = items;
            try
            {
                DistributeAndProcess(items.Count, adapter.Invoker);
            }
            finally
            {
                adapter.Items = null!;
            }
        }
        finally
        {
            // Running → Idle
            Interlocked.CompareExchange(ref _state, StateIdle, StateRunning);
        }
    }

    private void DistributeAndProcess(int count, Action<int> invoker)
    {
        // Setup shared state
        _invoker = invoker;
        _remainingItems = count;
        _exceptions = null;

        // Reset deques and distribute work round-robin
        for (int i = 0; i < _totalLanes; i++)
            _deques[i].Reset(count / _totalLanes + 1);

        for (int i = 0; i < count; i++)
            _deques[i % _totalLanes].PushBottom(i);

        // Wake workers
        _workAvailable.Set();

        // Main thread processes lane 0
        ProcessLane(0);

        // Wait for all workers to finish ProcessLane via barrier.
        // This guarantees no worker is still accessing deques or shared state
        // before the next wave begins.
        _waveBarrier.SignalAndWait();

        // All lanes done — _workAvailable was reset by the barrier's post-phase action
        _invoker = null;

        // Rethrow captured exceptions
        if (_exceptions is { IsEmpty: false })
        {
            throw new AggregateException(_exceptions.Select(e => e.SourceException));
        }
    }

    private void ProcessLane(int laneIndex)
    {
        var myDeque = _deques[laneIndex];

        while (true)
        {
            // Try own deque first (LIFO)
            int item = myDeque.PopBottom();

            if (item < 0)
            {
                // Try stealing from neighbors
                item = TrySteal(laneIndex);
                if (item < 0)
                {
                    // No work anywhere — check if all done
                    if (Volatile.Read(ref _remainingItems) <= 0)
                        return;

                    // Yield and retry — work might still be in-flight
                    Thread.Yield();
                    continue;
                }
            }

            // Execute the item
            try
            {
                _invoker!(item);
            }
            catch (Exception ex)
            {
                var queue = _exceptions;
                if (queue == null)
                {
                    Interlocked.CompareExchange(ref _exceptions, new ConcurrentQueue<ExceptionDispatchInfo>(), null);
                    queue = _exceptions;
                }
                queue!.Enqueue(ExceptionDispatchInfo.Capture(ex));
            }

            if (Interlocked.Decrement(ref _remainingItems) <= 0)
                return;
        }
    }

    private int TrySteal(int myLane)
    {
        // Round-robin steal from other lanes
        for (int offset = 1; offset < _totalLanes; offset++)
        {
            int victim = (myLane + offset) % _totalLanes;
            int item = _deques[victim].Steal();
            if (item >= 0)
                return item;
        }
        return -1;
    }

    private void WorkerLoop(int laneIndex)
    {
        while (true)
        {
            _workAvailable.Wait();

            if (_shutdown)
                return;

            ProcessLane(laneIndex);
            _waveBarrier.SignalAndWait();
        }
    }

    /// <summary>
    /// Shuts down all worker threads and releases resources.
    /// </summary>
    public void Dispose()
    {
        while (true)
        {
            var current = Interlocked.CompareExchange(ref _state, StateDisposing, StateIdle);
            if (current == StateIdle)
                break;
            if (current is StateDisposing or StateDisposed)
                return;
            Thread.SpinWait(1);
        }

        _shutdown = true;
        _workAvailable.Set();

        for (int i = 0; i < _workers.Length; i++)
            _workers[i].Join();

        _workAvailable.Dispose();
        _waveBarrier.Dispose();

        Volatile.Write(ref _state, StateDisposed);
    }

    private sealed class ItemsAdapter<T> where T : IWorkItem
    {
        public IReadOnlyList<T> Items = null!;
        public readonly Action<int> Invoker;

        public ItemsAdapter()
        {
            Invoker = Invoke;
        }

        private void Invoke(int index) => Items[index].Invoke();
    }
}
