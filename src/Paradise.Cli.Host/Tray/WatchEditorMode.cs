namespace Paradise.Cli;

/// <summary>
/// Live <c>--editor</c> for a watch session. The tray checkbox flips this; the loop reads it
/// on each rebuild so a click takes effect on the next change without restarting the watch.
/// </summary>
internal sealed class WatchEditorMode
{
    private int _on;

    public WatchEditorMode(bool on) => _on = on ? 1 : 0;

    /// <summary>Whether this watch writes <c>.editor/play</c> rather than <c>build/</c>.</summary>
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
