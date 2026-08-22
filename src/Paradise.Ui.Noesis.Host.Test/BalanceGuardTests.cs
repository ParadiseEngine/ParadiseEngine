namespace Paradise.Ui.Noesis.Host.Test;

/// <summary>
/// The documented pairing rule: "Update never blocks and allocates memory when not synchronized
/// with UpdateRenderTree", so an Update that returns true and is never matched by an
/// UpdateRenderTree queues a snapshot nobody collects.
///
/// A host drops frames for ordinary reasons — a minimized window, a lost swapchain, any frame
/// that returns before its overlay pass — so the guard has to hold without the host noticing.
/// These pin the two halves of it: ticking repeatedly with no render must not keep producing
/// snapshots, and a render must let the next one through.
/// </summary>
[NotInParallel]
public class BalanceGuardTests
{
    private const string Xaml = """
        <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              Background="#FF202020">
          <TextBlock x:Name="Label" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     Text="0" Foreground="White"/>
        </Grid>
        """;

    private static NoesisViewCore NewCore(string tag, Action? simTick = null)
    {
        var dir = Directory.CreateTempSubdirectory(tag).FullName;
        var path = Path.Combine(dir, "main.xaml");
        File.WriteAllText(path, Xaml);
        return new NoesisViewCore(path, 200, 100, simTick: simTick);
    }

    /// <summary>A UI that changes every tick, with the render side never collecting: the tick
    /// hook must stop being invoked rather than queueing a snapshot per frame forever.</summary>
    [Test]
    public async Task ticking_without_rendering_stops_producing_snapshots()
    {
        var ticks = 0;
        var core = NewCore("noesis-balance", () => ticks++);

        try { core.Input.Tick(0.0); }
        catch (DllNotFoundException ex) { Skip.Test($"Noesis native library not loadable: {ex.Message}"); return; }

        // 500 frames of a host that never reaches its overlay pass.
        for (var i = 1; i <= 500; i++)
        {
            core.Input.Tick(i / 60.0);
        }

        // The first tick creates the view and may legitimately produce one snapshot; nothing
        // after it may, because that one was never taken.
        await Assert.That(ticks).IsLessThanOrEqualTo(2);
    }

    /// <summary>...and collecting the frame must release the guard, or the UI would freeze the
    /// first time a host ever dropped a frame.</summary>
    [Test]
    public async Task rendering_lets_the_next_tick_through()
    {
        var ticks = 0;
        var core = NewCore("noesis-balance-resume", () => ticks++);

        try { core.Input.Tick(0.0); }
        catch (DllNotFoundException ex) { Skip.Test($"Noesis native library not loadable: {ex.Message}"); return; }

        core.Input.Tick(1.0 / 60.0);
        var afterStall = ticks;

        // The render thread collects; each following tick may then produce one more.
        for (var i = 2; i <= 10; i++)
        {
            core.TryUpdateRenderTree(out _);
            core.Input.Tick(i / 60.0);
        }

        await Assert.That(ticks).IsGreaterThan(afterStall);
    }
}
