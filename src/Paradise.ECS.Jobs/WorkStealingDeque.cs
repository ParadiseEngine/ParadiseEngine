namespace Paradise.ECS;

/// <summary>
/// Bounded lock-free Chase-Lev work-stealing deque.
/// The owner pushes/pops from the bottom (LIFO for cache locality),
/// while thieves steal from the top (FIFO for fairness).
/// </summary>
internal sealed class WorkStealingDeque
{
    private int[] _buffer;
    private int _top;
    private int _bottom;

    /// <summary>
    /// Initializes a new deque with the specified initial capacity.
    /// </summary>
    /// <param name="capacity">Initial capacity (must be power of 2).</param>
    public WorkStealingDeque(int capacity = 64)
    {
        _buffer = new int[RoundUpPowerOf2(capacity)];
    }

    /// <summary>
    /// Gets the current number of items in the deque (approximate, for diagnostics).
    /// </summary>
    public int Count => Math.Max(0, _bottom - _top);

    /// <summary>
    /// Owner pushes an item to the bottom of the deque.
    /// Only the owning thread may call this.
    /// </summary>
    /// <param name="item">The work item index to push.</param>
    public void PushBottom(int item)
    {
        int b = _bottom;
        int t = Volatile.Read(ref _top);
        int size = b - t;

        if (size >= _buffer.Length)
            Grow(b, t);

        _buffer[b & (_buffer.Length - 1)] = item;
        Volatile.Write(ref _bottom, b + 1);
    }

    /// <summary>
    /// Owner pops an item from the bottom of the deque (LIFO).
    /// Only the owning thread may call this.
    /// </summary>
    /// <returns>The work item index, or -1 if the deque is empty.</returns>
    public int PopBottom()
    {
        int b = _bottom - 1;
        _bottom = b;
        // StoreLoad fence: _bottom write must be visible to stealers before we read _top.
        // Volatile.Read is insufficient here — it only provides acquire (LoadLoad/LoadStore),
        // but we need the preceding store to _bottom ordered before the subsequent load of _top.
        Thread.MemoryBarrier();
        int t = _top;

        if (b - t >= 0)
        {
            // Non-empty
            int item = _buffer[b & (_buffer.Length - 1)];
            if (t == b)
            {
                // Last element — race with stealers
                if (Interlocked.CompareExchange(ref _top, t + 1, t) != t)
                {
                    // Lost race to a stealer
                    _bottom = t + 1;
                    return -1;
                }
                _bottom = t + 1;
            }
            return item;
        }

        // Empty
        _bottom = t;
        return -1;
    }

    /// <summary>
    /// Thief steals an item from the top of the deque (FIFO).
    /// Any thread may call this concurrently.
    /// </summary>
    /// <returns>The work item index, or -1 if the deque is empty or contention occurred.</returns>
    public int Steal()
    {
        int t = Volatile.Read(ref _top);
        // Acquire on _top ensures we read _top before _bottom (LoadLoad ordering).
        // Plain read of _bottom is intentional per Chase-Lev: a stale value only causes
        // a missed steal opportunity, and the CAS on _top guarantees correctness.
        int b = _bottom;

        if (b - t <= 0)
            return -1; // Empty

        // Cache buffer and length to avoid race with Grow()
        var buffer = _buffer;
        int item = buffer[t & (buffer.Length - 1)];

        if (Interlocked.CompareExchange(ref _top, t + 1, t) != t)
            return -1; // Lost race

        return item;
    }

    /// <summary>
    /// Resets the deque for reuse across waves without reallocation.
    /// Only call when no threads are accessing the deque.
    /// Resetting to 0 prevents index wrap-around issues within a single wave.
    /// </summary>
    /// <param name="capacity">Optional new minimum capacity.</param>
    public void Reset(int capacity = 0)
    {
        _top = 0;
        _bottom = 0;
        if (capacity > _buffer.Length)
            _buffer = new int[RoundUpPowerOf2(capacity)];
    }

    private void Grow(int bottom, int top)
    {
        int oldLen = _buffer.Length;
        int newLen = oldLen * 2;
        var newBuf = new int[newLen];

        // Use size to avoid wrap-around bugs in loop condition
        int size = bottom - top;
        for (int i = 0; i < size; i++)
        {
            int index = top + i;
            newBuf[index & (newLen - 1)] = _buffer[index & (oldLen - 1)];
        }
        _buffer = newBuf;
    }

    private static int RoundUpPowerOf2(int value)
    {
        if (value <= 1) return 1;
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }
}
