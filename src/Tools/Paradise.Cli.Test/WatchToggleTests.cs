namespace Paradise.Cli.Test;

public class WatchToggleTests
{
    [Test]
    public async Task starts_on_when_asked()
    {
        await Assert.That(new WatchToggle(true).IsOn).IsTrue();
        await Assert.That(new WatchToggle(false).IsOn).IsFalse();
    }

    [Test]
    public async Task toggle_flips_and_returns_the_new_value()
    {
        var mode = new WatchToggle(true);

        await Assert.That(mode.Toggle()).IsFalse();
        await Assert.That(mode.IsOn).IsFalse();
        await Assert.That(mode.Toggle()).IsTrue();
        await Assert.That(mode.IsOn).IsTrue();
    }
}
