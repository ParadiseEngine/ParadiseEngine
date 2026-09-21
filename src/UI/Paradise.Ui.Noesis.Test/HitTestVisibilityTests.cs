using Paradise.Windowing;
using Zio;
using Zio.FileSystems;

namespace Paradise.Ui.Noesis.Test;

/// <summary>Checks overlay hit-test authoring rules.</summary>
/// <remarks>A false IsHitTestVisible excludes the entire subtree, including children set true. Keep
/// the root hit-testable with a null background and disable hit-testing only on decorative
/// elements.</remarks>
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

    /// <summary>An empty overlay must let pointer presses reach the game.</summary>
    /// <remarks>Noesis 4.0.0 MouseButtonDown returns true even over an empty view, so the input
    /// wrapper must hit-test both pointer buttons.</remarks>
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
