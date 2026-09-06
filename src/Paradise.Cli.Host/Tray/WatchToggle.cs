namespace Paradise.Cli;

/// <summary>
/// A live flag for a watch session (<c>--editor</c>, restart-on-scene-save). The tray checkbox
/// flips it; the loop reads it on each change so a click takes effect without restarting the watch.
/// </summary>
internal sealed class WatchToggle
{
    private int _on;

    public WatchToggle(bool on) => _on = on ? 1 : 0;

    public bool IsOn => Volatile.Read(ref _on) != 0;

    /// <summary>Flip and return the new value.</summary>
    public bool Toggle()
    {
        while (true)
        {
            var current = Volatile.Read(ref _on);
            var next = current == 0 ? 1 : 0;
            if (Interlocked.CompareExchange(ref _on, next, current) == current)
            {
                return next != 0;
            }
        }
    }
}
