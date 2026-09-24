namespace Paradise.Cli;

/// <summary>An atomic watch-session flag controlled by the tray.</summary>
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
