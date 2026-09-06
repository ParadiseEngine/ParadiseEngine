using System.Diagnostics;

namespace Paradise.Cli.Test;

/// <summary>The real runner, against a real child: what the fakes in <see cref="HostSessionTests"/> stand in for.</summary>
public class ConsoleProcessRunnerTests
{
    [Test]
    public async Task a_started_callback_that_throws_takes_the_child_down_with_it()
    {
        if (OperatingSystem.IsWindows()) return;

        var runner = new ConsoleProcessRunner();
        var pid = 0;
        var spec = new ProcessSpec("/bin/sleep", ["30"], Path.GetTempPath());

        await Assert.That(() => runner.Run(spec, CancellationToken.None, started: id =>
        {
            pid = id;
            throw new InvalidOperationException("no watcher for you");
        })).Throws<InvalidOperationException>();

        await Assert.That(pid).IsNotEqualTo(0);
        await Assert.That(IsAlive(pid)).IsFalse();
    }

    [Test]
    public async Task a_stop_ends_the_child_and_reports_interrupted()
    {
        if (OperatingSystem.IsWindows()) return;

        var runner = new ConsoleProcessRunner();
        using var stop = new CancellationTokenSource();
        var pid = 0;
        var spec = new ProcessSpec("/bin/sleep", ["30"], Path.GetTempPath());

        var run = Task.Run(() => runner.Run(spec, stop.Token, started: id => { pid = id; stop.CancelAfter(200); }));

        var exit = await run.ConfigureAwait(false);
        await Assert.That(exit).IsEqualTo(ConsoleProcessRunner.Interrupted);
        await Assert.That(IsAlive(pid)).IsFalse();
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
