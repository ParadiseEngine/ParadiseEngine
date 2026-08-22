using Paradise.Windowing;
namespace Paradise.Ui.Noesis.Host.Test;

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
    private static (bool Handled, int Reached)? ClickCentre(string xaml, string tag)
    {
        var dir = Directory.CreateTempSubdirectory(tag).FullName;
        var path = Path.Combine(dir, "main.xaml");
        File.WriteAllText(path, xaml);
        var core = new NoesisViewCore(path, 200, 100);

        try { core.Input.Tick(0.0); }
        catch (DllNotFoundException) { return null; }

        var inner = (global::Noesis.FrameworkElement)
            ((global::Noesis.FrameworkElement)core.View!.Content).FindName("Inner");
        var reached = 0;
        inner.MouseLeftButtonDown += (_, e) => { reached++; e.Handled = true; };

        core.TryUpdateRenderTree(out _);
        core.Input.Tick(1.0 / 60.0);
        _ = core.Input.Handle(UiEvent.PointerMove(100f, 50f));
        var handled = core.Input.Handle(
            UiEvent.PointerDown(100f, 50f, PointerButton.Left, default, default));
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
            """, "hit-nested");
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
            """, "hit-fixed");
        if (result is not { } r) { Skip.Test("Noesis native library not loadable"); return; }

        await Assert.That(r.Reached).IsEqualTo(1);
        await Assert.That(r.Handled).IsTrue();
    }
}
