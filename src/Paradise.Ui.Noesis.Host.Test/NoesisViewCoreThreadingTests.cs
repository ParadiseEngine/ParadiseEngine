using Paradise.Windowing;
namespace Paradise.Ui.Noesis.Host.Test;

/// <summary>
/// The threading invariant a two-thread host depends on: a View created and updated on one
/// thread still routes input delivered from ANOTHER thread, correctly and without tearing,
/// because <see cref="NoesisViewCore"/> serializes the two behind its sync lock.
///
/// This is what lets a host put the UI's view and <c>View.Update</c> on the RENDER thread —
/// where its ViewModel can read presentation state directly — while the GAME thread keeps
/// asking "did the UI consume this?" synchronously as it drains raw input. Without it, that
/// split silently stops blocking input and every click reaches the game twice.
///
/// Read the verdict carefully: <c>View.MouseButtonDown</c> reports HANDLED, not HIT. A bare
/// Grid or Rectangle is hit-testable but handles nothing, so it returns false — which is why
/// the handlers below mark the event handled, and why a host relying on this for input blocking
/// must put something that actually handles input under the pointer.
/// </summary>
[NotInParallel]
public class NoesisViewCoreThreadingTests
{
    private const string Xaml = """
        <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              Background="#FF202020"/>
        """;

    private sealed class Counters { public int Moves, Downs, Ups; }

    private static NoesisViewCore NewCore(string tag)
    {
        var dir = Directory.CreateTempSubdirectory(tag).FullName;
        var path = Path.Combine(dir, "main.xaml");
        File.WriteAllText(path, Xaml);
        return new NoesisViewCore(path, 200, 100);
    }

    /// <summary>Count what reaches the tree and mark it handled. View thread only.</summary>
    private static void Instrument(NoesisViewCore core, Counters c)
    {
        var root = (global::Noesis.FrameworkElement)core.View!.Content;
        root.MouseMove += (_, _) => Interlocked.Increment(ref c.Moves);
        root.MouseLeftButtonDown += (_, e) => { Interlocked.Increment(ref c.Downs); e.Handled = true; };
        root.MouseLeftButtonUp += (_, e) => { Interlocked.Increment(ref c.Ups); e.Handled = true; };
    }

    private static void Click(NoesisViewCore core, out bool down, out bool up)
    {
        _ = core.Input.Handle(WindowEvent.PointerMove(100f, 50f));
        down = core.Input.Handle(WindowEvent.Mouse(PointerButton.Left, pressed: true, 100f, 50f));
        up = core.Input.Handle(WindowEvent.Mouse(PointerButton.Left, pressed: false, 100f, 50f));
    }

    /// <summary>The baseline the cross-thread case is compared against — without it, a false
    /// verdict there is uninterpretable (it could just mean the click missed).</summary>
    [Test]
    public async Task input_on_the_views_own_thread_is_handled()
    {
        var core = NewCore("noesis-same-thread");
        try { core.Input.Tick(0.0); }
        catch (DllNotFoundException ex) { Skip.Test($"Noesis native library not loadable: {ex.Message}"); return; }

        var counters = new Counters();
        Instrument(core, counters);
        core.Input.Tick(1.0 / 60.0);
        Click(core, out var down, out var up);

        await Assert.That(down).IsTrue();
        await Assert.That(up).IsTrue();
        await Assert.That(counters.Downs).IsEqualTo(1);
    }

    /// <summary>The invariant itself: the view is created and ticked on a worker standing in for
    /// the render thread, and every click is delivered from THIS thread standing in for the game
    /// thread. Both the routing and the handled verdict must survive it.</summary>
    [Test]
    public async Task input_from_another_thread_reaches_the_view_and_reports_its_verdict()
    {
        const int Rounds = 100;
        var core = NewCore("noesis-cross-thread");
        var counters = new Counters();
        Exception? ownerFailure = null;
        using var ready = new ManualResetEventSlim();
        using var stop = new ManualResetEventSlim();

        var owner = new Thread(() =>
        {
            try
            {
                core.Input.Tick(0.0); // the view is created HERE, and pinned to this thread
                Instrument(core, counters);
                core.Input.Tick(1.0 / 60.0);
                ready.Set();
                var frame = 2.0;
                while (!stop.IsSet)
                {
                    core.Input.Tick(++frame / 60.0);
                    Thread.Sleep(2);
                }
            }
            catch (Exception ex) { ownerFailure = ex; ready.Set(); }
        }) { IsBackground = true, Name = "noesis-view-owner" };

        owner.Start();
        ready.Wait(TimeSpan.FromSeconds(30));

        if (ownerFailure is DllNotFoundException dll)
        {
            stop.Set();
            Skip.Test($"Noesis native library not loadable: {dll.Message}");
            return;
        }

        bool down = false, up = false;
        Exception? inputFailure = null;
        try
        {
            for (var i = 0; i < Rounds; i++)
            {
                Click(core, out down, out up);
            }
        }
        catch (Exception ex) { inputFailure = ex; }

        stop.Set();
        owner.Join(TimeSpan.FromSeconds(5));

        await Assert.That(inputFailure).IsNull();
        await Assert.That(ownerFailure).IsNull();
        await Assert.That(down).IsTrue();
        await Assert.That(up).IsTrue();
        await Assert.That(counters.Downs).IsEqualTo(Rounds);
        await Assert.That(counters.Ups).IsEqualTo(Rounds);
    }
}
