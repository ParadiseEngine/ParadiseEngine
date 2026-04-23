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
    /// <summary>
    /// Number of work items claimed per atomic operation to reduce contention.
    /// </summary>
    private const int BatchSize = 8;

    private const int StateIdle = 0;
    private const int StateRunning = 1;
    private const int StateDisposing = 2;
    private const int StateDisposed = 3;

    /// <summary>
    /// Cache-line padded counters to prevent false sharing between threads.
    /// NextWorkIndex and RemainingItems are on separate 64-byte cache lines.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct PaddedCounters
    {
        [FieldOffset(0)]
        public int NextWorkIndex;

        [FieldOffset(64)]
        public int RemainingItems;
    }

    private readonly Thread[] _workers;
    private readonly int _workerCount;

    // Per-wave state
    private Action<int>? _invoker;
    private int _itemCount;
    private PaddedCounters _counters;
    private ConcurrentQueue<ExceptionDispatchInfo>? _exceptions;

    // One-shot latch flipped by the single thread that performs end-of-wave
    // bookkeeping (reset _workAvailable, then set _workComplete). Reset to 0 at
    // the start of each wave. Multiple draining threads may observe
    // RemainingItems <= 0 simultaneously; the latch ensures exactly one of them
    // does the cleanup, preventing a stray late Reset() from clobbering the
    // next wave's Set() on _workAvailable.
    private int _waveCompleteLatch;

    // Synchronization
    private readonly ManualResetEventSlim _workAvailable = new(false);
    private readonly ManualResetEventSlim _workComplete = new(false);
    private volatile bool _shutdown;
    private int _state;

    // Cached adapter for zero-allocation item dispatch
    private object? _cachedAdapter;

    /// <summary>
    /// Gets the number of worker threads (excludes the main thread).
    /// </summary>
    public int WorkerCount => _workerCount;

    /// <summary>
    /// Initializes a new <see cref="JobWorkerPool"/> with the specified number of worker threads.
    /// </summary>
    /// <param name="workerCount">
    /// Number of background worker threads. Defaults to <c>Environment.ProcessorCount - 1</c> (minimum 1).
    /// The calling thread also participates in work, so total parallelism is <paramref name="workerCount"/> + 1.
    /// </param>
    public JobWorkerPool(int workerCount = -1)
    {
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
        // State machine: Idle → Running
        var previousState = Interlocked.CompareExchange(ref _state, StateRunning, StateIdle);
        switch (previousState)
        {
            case StateRunning:
                throw new InvalidOperationException("JobWorkerPool.ExecuteWork is not reentrant. A concurrent call is already in progress.");
            case StateDisposing:
            case StateDisposed:
                throw new ObjectDisposedException(nameof(JobWorkerPool));
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
            // Running → Idle (allow next call or disposal)
            Interlocked.CompareExchange(ref _state, StateIdle, StateRunning);
        }
    }

    private void DistributeAndProcess(int count, Action<int> invoker)
    {
        // Setup shared state before waking workers
        _invoker = invoker;
        _itemCount = count;
        _counters.NextWorkIndex = 0;
        _counters.RemainingItems = count;
        _exceptions = null;
        Volatile.Write(ref _waveCompleteLatch, 0);

        // Reset completion signal, then wake workers. The Set publishes all the
        // shared-state writes above (memory fence). _workAvailable is guaranteed
        // to be in the reset state here: the previous wave's completer (whichever
        // draining thread won the latch) called Reset() BEFORE Setting
        // _workComplete, so by the time main returned from _workComplete.Wait()
        // last wave, _workAvailable was already false.
        _workComplete.Reset();
        _workAvailable.Set();

        // Main thread participates in draining work
        DrainWork();

        // Wait for all items to complete. The completer worker has already reset
        // _workAvailable, so any worker looping back into WorkerLoop will block
        // on Wait() until the next wave's Set() — no busy re-entry into DrainWork.
        _workComplete.Wait();

        _invoker = null;

        // Rethrow captured exceptions
        if (_exceptions is { IsEmpty: false })
        {
            throw new AggregateException(_exceptions.Select(e => e.SourceException));
        }
    }

    private void DrainWork()
    {
        while (true)
        {
            int start = Interlocked.Add(ref _counters.NextWorkIndex, BatchSize) - BatchSize;
            if (start >= _itemCount)
                return;

            int end = Math.Min(start + BatchSize, _itemCount);
            int batchCount = end - start;

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

            if (Interlocked.Add(ref _counters.RemainingItems, -batchCount) <= 0)
            {
                // Multiple draining threads may observe RemainingItems <= 0 (the
                // last batch may straddle batchSize boundaries, dropping the
                // counter below zero from several threads at once). Use a
                // one-shot latch so exactly one thread performs the wave-end
                // bookkeeping. Otherwise a stray late Reset() from a slower
                // completer could clobber the next wave's _workAvailable.Set().
                if (Interlocked.Exchange(ref _waveCompleteLatch, 1) == 0)
                {
                    // Reset BEFORE signaling so workers looping back to
                    // WorkerLoop.Wait() block until the next wave, instead of
                    // re-entering DrainWork on a still-set _workAvailable.
                    _workAvailable.Reset();
                    _workComplete.Set();
                }
                return;
            }
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            _workAvailable.Wait();

            if (_shutdown)
                return;

            DrainWork();
        }
    }

    /// <summary>
    /// Shuts down all worker threads and releases resources.
    /// Blocks until all workers have terminated. If work is currently executing,
    /// waits for it to complete before shutting down.
    /// </summary>
    public void Dispose()
    {
        // Attempt Idle → Disposing, spin-wait if Running
        while (true)
        {
            var current = Interlocked.CompareExchange(ref _state, StateDisposing, StateIdle);
            if (current == StateIdle)
                break; // Successfully transitioned to Disposing
            if (current is StateDisposing or StateDisposed)
                return; // Already disposing/disposed — idempotent
            // StateRunning: work in progress — spin-wait for it to finish
            Thread.SpinWait(1);
        }

        _shutdown = true;
        _workAvailable.Set();

        for (int i = 0; i < _workers.Length; i++)
            _workers[i].Join();

        _workAvailable.Dispose();
        _workComplete.Dispose();

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
