using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Paradise.ECS;

/// <summary>
/// Persistent worker thread pool for parallel job execution.
/// Maintains a fixed set of background threads that sleep between waves,
/// eliminating per-wave thread startup overhead compared to <see cref="System.Threading.Tasks.Parallel"/>.
/// The main (calling) thread participates in work processing for N+1 total parallelism.
/// </summary>
public sealed class JobWorkerPool : IDisposable
{
    /// <summary>Number of work items claimed per atomic operation to reduce contention.</summary>
    private const int BatchSize = 8;

    private const int StateIdle = 0;
    private const int StateRunning = 1;
    private const int StateDisposing = 2;
    private const int StateDisposed = 3;

    // Keep the contended batch counter away from the read-mostly wave fields.
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct PaddedCounter
    {
        [FieldOffset(64)]
        public int NextWorkIndex;
    }

    private readonly Thread[] _workers;
    private readonly int _workerCount;
    private readonly object _gate = new();
    private readonly Action<int, bool>? _afterBatchClaim;

    // Publication, drainer admission and retirement share one gate. Completing the
    // last item is insufficient: another drainer may still hold an out-of-range
    // claim. No wave state or adapter is reused until every admitted drainer exits.
    private Action<int>? _invoker;
    private int _itemCount;
    private PaddedCounter _counter;
    private ConcurrentQueue<ExceptionDispatchInfo>? _exceptions;
    private long _generation;
    private int _activeDrainers;
    private bool _shutdown;
    private int _state;
    private Thread? _callingThread;

    // Cached adapter for zero-allocation item dispatch
    private object? _cachedAdapter;

    /// <summary>The number of worker threads (excludes the main thread).</summary>
    public int WorkerCount => _workerCount;

    /// <summary>Initializes a new <see cref="JobWorkerPool"/> with the specified number of worker threads.</summary>
    /// <param name="workerCount">
    /// Number of background worker threads. Defaults to <c>Environment.ProcessorCount - 1</c> (minimum 1).
    /// The calling thread also participates in work, so total parallelism is <paramref name="workerCount"/> + 1.
    /// </param>
    public JobWorkerPool(int workerCount = -1) : this(workerCount, null)
    {
    }

    // The hook pauses a claimed batch before its range check in regression tests.
    internal JobWorkerPool(int workerCount, Action<int, bool>? afterBatchClaim)
    {
        _afterBatchClaim = afterBatchClaim;
        _workerCount = workerCount < 0
            ? Math.Max(1, Environment.ProcessorCount - 1)
            : Math.Max(1, workerCount);

        _workers = new Thread[_workerCount];
        for (int i = 0; i < _workerCount; i++)
        {
            var worker = new Thread(WorkerLoop)
            {
                Name = $"Paradise.ECS Worker {i}",
                IsBackground = true,
            };
            _workers[i] = worker;
            worker.Start();
        }
    }

    /// <summary>
    /// Distributes work items across all threads (workers + main).
    /// Blocks until all items are processed. Each item is processed exactly once.
    /// If any work item throws, all exceptions are captured and rethrown as an <see cref="AggregateException"/>
    /// after all items complete. The pool remains usable after an exception.
    /// </summary>
    /// <typeparam name="T">The work item type implementing <see cref="IWorkItem"/>.</typeparam>
    /// <param name="items">The work items to process.</param>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The pool is already executing work (reentrant call).</exception>
    /// <exception cref="AggregateException">One or more work items threw exceptions.</exception>
    public void ExecuteWork<T>(IReadOnlyList<T> items) where T : IWorkItem
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state is StateDisposing or StateDisposed, this);
            if (_state == StateRunning)
            {
                throw new InvalidOperationException("JobWorkerPool.ExecuteWork is not reentrant. A concurrent call is already in progress.");
            }

            _state = StateRunning;
            _callingThread = Thread.CurrentThread;
        }

        try
        {
            switch (items.Count)
            {
                case <= 0:
                    return;
                case 1:
                    // Wrap single-item exceptions in AggregateException so the
                    // exception shape is consistent with the multi-item path
                    // (documented in this method's <exception> tag). Otherwise
                    // callers writing `catch (AggregateException)` would silently
                    // miss exceptions thrown from one-item waves.
                    try
                    {
                        items[0].Invoke();
                    }
                    catch (Exception ex)
                    {
                        throw new AggregateException(ex);
                    }
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
            lock (_gate)
            {
                _callingThread = null;
                _state = StateIdle;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void DistributeAndProcess(int count, Action<int> invoker)
    {
        lock (_gate)
        {
            _invoker = invoker;
            _itemCount = count;
            _counter.NextWorkIndex = 0;
            _exceptions = null;
            _activeDrainers = 1; // The caller is admitted before any worker can join.
            _generation++;
            Monitor.PulseAll(_gate);
        }

        try
        {
            DrainWork(isWorker: false);
        }
        finally
        {
            CompleteDraining();
            lock (_gate)
            {
                while (_activeDrainers != 0)
                    Monitor.Wait(_gate);
            }
        }

        _invoker = null;

        if (_exceptions is { IsEmpty: false })
        {
            throw new AggregateException(_exceptions.Select(e => e.SourceException));
        }
    }

    private void DrainWork(bool isWorker)
    {
        while (true)
        {
            int start = Interlocked.Add(ref _counter.NextWorkIndex, BatchSize) - BatchSize;
            _afterBatchClaim?.Invoke(start, isWorker);
            if (start >= _itemCount)
                return;

            int end = Math.Min(start + BatchSize, _itemCount);
            for (int i = start; i < end; i++)
            {
                try
                {
                    _invoker!(i);
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
            }
        }
    }

    private void CompleteDraining()
    {
        lock (_gate)
        {
            // Closing admission and publishing quiescence are the same operation.
            // A worker that wakes late either joins a still-live wave under this
            // gate or waits for another generation without touching its fields.
            if (--_activeDrainers == 0)
                Monitor.PulseAll(_gate);
        }
    }

    private void WorkerLoop()
    {
        long observedGeneration = 0;
        while (true)
        {
            lock (_gate)
            {
                while (!_shutdown && (_activeDrainers == 0 || observedGeneration == _generation))
                    Monitor.Wait(_gate);

                if (_shutdown)
                    return;

                observedGeneration = _generation;
                _activeDrainers++;
            }

            try
            {
                DrainWork(isWorker: true);
            }
            finally
            {
                CompleteDraining();
            }
        }
    }

    /// <summary>Shuts down all worker threads and releases resources after in-flight work completes.</summary>
    /// <exception cref="InvalidOperationException">Called from this pool's work callback.</exception>
    public void Dispose()
    {
        lock (_gate)
        {
            // Waiting here from a callback would wait for that same callback to
            // return. Include the caller's single-item fast path and nested pools.
            if (_state == StateRunning &&
                (Thread.CurrentThread == _callingThread || Array.IndexOf(_workers, Thread.CurrentThread) >= 0))
            {
#pragma warning disable CA1065 // Self-disposal must fail before mutation: waiting would deadlock on this callback.
                throw new InvalidOperationException("JobWorkerPool cannot be disposed from its own work callback.");
#pragma warning restore CA1065
            }

            while (_state == StateRunning)
                Monitor.Wait(_gate);

            if (_state is StateDisposing or StateDisposed)
                return;

            _state = StateDisposing;
            _shutdown = true;
            Monitor.PulseAll(_gate);
        }

        for (int i = 0; i < _workers.Length; i++)
            _workers[i].Join();

        lock (_gate)
        {
            _state = StateDisposed;
        }
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
