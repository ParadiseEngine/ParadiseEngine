using Paradise.Windowing;

namespace Paradise.Hosting.Test;

public class HostCommandLineTests
{
    [Test]
    public async Task HeadlessCaptureDefaultsToTheFinalFrame()
    {
        var options = HostCommandLine.Parse(["--headless", "--screenshot", "shot.png"], new WindowOptions("Test", 64, 32));
        await Assert.That(options.FrameLimit).IsEqualTo(120);
        await Assert.That(options.Capture!.Schedule().Single()).IsEqualTo(120);
    }

    [Test]
    public async Task FrameListIsOrderedAndDuplicatesDoNotQueueMultipleReadbacks()
    {
        var options = HostCommandLine.Parse(
            ["--headless", "--frames", "200", "--screenshot", "shot.png", "--capture-frame", "90,30,90"],
            new WindowOptions("Test", 64, 32));
        await Assert.That(string.Join(',', options.Capture!.Schedule())).IsEqualTo("30,90");
        await Assert.That(options.Capture.PathFor(30)).IsEqualTo("shot-00030.png");
    }

    [Test]
    [Arguments("--frames")]
    [Arguments("--frames", "wrong")]
    [Arguments("--frames", "0")]
    [Arguments("--frames", "--headless")]
    [Arguments("--hold", "W")]
    [Arguments("--capture-frame", "30")]
    public void InvalidOptionsAreRefused(params string[] args)
    {
        Assert.Throws<FormatException>(() => HostCommandLine.Parse(args, new WindowOptions("Test", 64, 32)));
    }

    [Test]
    public void ImpossibleCaptureIsRefused()
    {
        Assert.Throws<FormatException>(() => HostCommandLine.Parse(
            ["--headless", "--frames", "10", "--screenshot", "shot.png", "--capture-every", "30"],
            new WindowOptions("Test", 64, 32)));
    }

    [Test]
    public async Task HeadlessInitialInputPreservesTimestampAndCloseIsLatched()
    {
        using var platform = new HeadlessWindowPlatform([KeyboardKey.W]);
        using var window = platform.CreateWindow(new WindowOptions("Test", 64, 32));
        await Assert.That(window.TryReadEvent(out var input)).IsTrue();
        await Assert.That(input.Timestamp).IsEqualTo(TimeSpan.Zero);
        await Assert.That(input.Event).IsEqualTo(WindowEvent.Keyboard(KeyboardKey.W, true));
        await Assert.That(window.TryReadEvent(out _)).IsFalse();
        window.RequestClose();
        platform.Pump();
        await Assert.That(window.CloseRequested).IsTrue();
    }
}
