using Paradise.Windowing;
using Zio;
using Zio.FileSystems;

namespace Paradise.Ui.Noesis.Test;

/// <summary>
/// How a game overlay must be authored, and the trap that reads as "the menu is dead".
///
/// The tempting shape for a HUD-plus-menu is a root with <c>IsHitTestVisible="False"</c> so
/// clicks fall through to the game, and the menu setting it back to <c>True</c> so it catches
/// them. That does not work: a False parent excludes its ENTIRE SUBTREE, and a child cannot
/// re-enable itself. Nothing warns — the overlay still draws, the click just reaches nothing —
/// so the failure looks like broken pointer input several layers down.
///
/// Hit-testing is therefore OPT-OUT: keep the root hit-testable with a null Background (so
/// empty regions are not targets and clicks there fall through), and set
/// <c>IsHitTestVisible="False"</c> on each piece of PAINT that has a background of its own.
/// </summary>
[NotInParallel]
public class HitTestVisibilityTests
{
    private const string Header = """
        <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        """;

    /// <summary>Click the centre and report whether the inner element caught it. Null when
    /// Noesis is unavailable.</summary>
    /// <param name="hasInner">Whether the XAML declares an <c>Inner</c> element to watch.</param>
    private static (bool Handled, int Reached)? ClickCentre(string xaml, bool hasInner = true)
    {
        var content = new MemoryFileSystem();
        content.WriteAllText("/main.xaml", xaml);
        var core = new NoesisViewCore(content, "/main.xaml", 200, 100);

        try { core.Input.Tick(0.0); }
        catch (DllNotFoundException) { return null; }

        var reached = 0;
        if (hasInner)
        {
            var inner = (global::Noesis.FrameworkElement)
                ((global::Noesis.FrameworkElement)core.View!.Content).FindName("Inner");
            inner.MouseLeftButtonDown += (_, e) => { reached++; e.Handled = true; };
        }

        core.TryUpdateRenderTree(out _);
        core.Input.Tick(1.0 / 60.0);
        _ = core.Input.Handle(WindowEvent.PointerMove(100f, 50f));
        var handled = core.Input.Handle(
            WindowEvent.Mouse(PointerButton.Left, pressed: true, 100f, 50f));
        return (handled, reached);
    }

    [Test]
    public async Task a_false_parent_blocks_a_true_child()
    {
        var result = ClickCentre(
            Header + """
                      Background="{x:Null}" IsHitTestVisible="False">
              <Grid x:Name="Inner" IsHitTestVisible="True" Background="#FF203040"/>
            </Grid>
            """);
        if (result is not { } r) { Skip.Test("Noesis native library not loadable"); return; }

        await Assert.That(r.Reached).IsEqualTo(0);
        await Assert.That(r.Handled).IsFalse();
    }

    [Test]
    public async Task a_hit_testable_root_with_a_null_background_lets_a_child_catch_clicks()
    {
        var result = ClickCentre(
            Header + """
                      Background="{x:Null}">
              <Grid x:Name="Inner" Background="#FF203040"/>
            </Grid>
            """);
        if (result is not { } r) { Skip.Test("Noesis native library not loadable"); return; }

        await Assert.That(r.Reached).IsEqualTo(1);
        await Assert.That(r.Handled).IsTrue();
    }

    /// <summary>
    /// An overlay with nothing in it swallows nothing — the whole point of the verdict.
    /// </summary>
    /// <remarks>
    /// The narrowest possible statement of what the host relies on, and the case that was
    /// silently broken: Noesis 4.0.0's <c>View.MouseButtonDown</c> returns true even here, where
    /// the view is one empty Grid with a null background and there is nothing under the pointer
    /// at all. Forwarded to the host as-is, that is a total mouse blackout — a HUD that draws
    /// nothing eats every click in the game. Left-and-right, because the press is the only event
    /// kind affected and both buttons showed it.
    /// </remarks>
    [Test]
    public async Task an_empty_overlay_does_not_swallow_the_click()
    {
        var result = ClickCentre(
            Header + """
                      Background="{x:Null}"/>
            """, hasInner: false);
        if (result is not { } r) { Skip.Test("Noesis native library not loadable"); return; }

        await Assert.That(r.Handled).IsFalse();
    }
}
