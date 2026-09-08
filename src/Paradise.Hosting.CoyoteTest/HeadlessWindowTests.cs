using Microsoft.Coyote.Specifications;
using Paradise.Windowing;

namespace Paradise.Hosting.CoyoteTest;

public static class HeadlessWindowTests
{
    public static async Task ConcurrentInputAndClosePreserveEachTransition()
    {
        using var platform = new HeadlessWindowPlatform([KeyboardKey.W, KeyboardKey.A]);
        using var window = platform.CreateWindow(new WindowOptions("Test", 64, 32));
        var first = new List<TimedWindowEvent>();
        var second = new List<TimedWindowEvent>();
        var a = Task.Run(() =>
        {
            while (window.TryReadEvent(out var input)) first.Add(input);
            window.RequestClose();
        });
        var b = Task.Run(() =>
        {
            while (window.TryReadEvent(out var input)) second.Add(input);
            window.RequestClose();
        });
        await a.ConfigureAwait(false);
        await b.ConfigureAwait(false);
        Specification.Assert(window.CloseRequested, "Close requests must remain latched.");
        Specification.Assert(first.Count + second.Count == 2, "Each initial input must be consumed once.");
        var combined = first.Concat(second).Select(e => e.Event.KeyboardKey).ToArray();
        Specification.Assert(combined.Distinct().Count() == 2, "Input must not be delivered twice.");
    }
}
