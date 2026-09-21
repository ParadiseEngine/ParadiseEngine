using Paradise.Features;
using Zio;
using Zio.FileSystems;

namespace Paradise.Hosting.Test;

public class HostConfigurationTests
{
    [Test]
    public async Task LayersApplyBeforeGameAndRendererRegisterTheirFeatures()
    {
        using var files = new MemoryFileSystem();
        files.WriteAllText("/engine.toml", """
            [[features]]
            name = "game.movement"
            enabled = false
            speed = 4

            [[features]]
            name = "rendering.bloom"
            enabled = true
            """);
        var switches = HostConfiguration.Load(files, "/engine.toml", "+game.movement,-rendering.bloom", "-game.movement");
        var movement = switches.Declare("game.movement");
        var bloom = switches.Declare("rendering.bloom");
        await Assert.That(switches.IsEnabled(movement.Id)).IsFalse();
        await Assert.That(switches.IsEnabled(bloom.Id)).IsFalse();
        await Assert.That(switches.SettingsFor(movement.Id).Text).Contains("speed = 4");
        await Assert.That(switches.Unknown.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MissingDefaultFileKeepsDeclarationsAndUnknownOverrides()
    {
        using var files = new MemoryFileSystem();
        var switches = HostConfiguration.Load(files, "/engine.toml", null, "-rendering.typo");
        var feature = switches.Declare("game.movement");
        await Assert.That(switches.IsEnabled(feature.Id)).IsTrue();
        await Assert.That(switches.Unknown.Count).IsEqualTo(1);
    }

    [Test]
    public void BrokenFileIsNotSilentlyReplacedWithDefaults()
    {
        using var files = new MemoryFileSystem();
        files.WriteAllText("/engine.toml", "[[features]]\nname = ");
        Assert.Throws<FormatException>(() => HostConfiguration.Load(files, "/engine.toml", null, null));
    }
}
