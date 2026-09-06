using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Paradise.Cli;

/// <summary>One child process: what to run, with what, and from where. Output is inherited, never captured — the caller's console (or the log Blender pipes it into) is where a build error and the game's own lines belong.</summary>
internal sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public string CommandLine => FileName + " " + string.Join(' ', Arguments.Select(Quote));

    private static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(char.IsWhiteSpace) ? argument : $"\"{argument}\"";
}

/// <summary>The seam <see cref="HostSession"/> is tested through: a fake records the specs, the real one starts them.</summary>
internal interface IProcessRunner
{
    /// <summary>Runs to exit and returns the exit code; a <paramref name="stop"/> request kills the process tree and returns non-zero. <paramref name="started"/> gets the pid once it is running.</summary>
    int Run(ProcessSpec spec, CancellationToken stop, Action<int>? started = null);
}

internal sealed class ConsoleProcessRunner : IProcessRunner
{
    /// <summary>The exit code reported when <c>stop</c> ended the child; 130 is the shell's own code for an interrupted command.</summary>
    public const int Interrupted = 130;

    public int Run(ProcessSpec spec, CancellationToken stop, Action<int>? started = null)
    {
        if (stop.IsCancellationRequested) return Interrupted;

        var start = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
        };
        foreach (var argument in spec.Arguments) start.ArgumentList.Add(argument);

        // Two `dotnet` invocations sharing an MSBuild server die on MSB0001 "Invalid node id":
        // the Blender addon's asset watcher, a `dotnet watch` and this build can all be alive at
        // once, so every child this verb starts stays off the server.
        start.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
        // dotnet watch: a rude edit restarts the game instead of asking a console nobody reads.
        start.Environment["DOTNET_WATCH_RESTART_ON_RUDE_EDIT"] = "1";
        start.Environment["DOTNET_WATCH_SUPPRESS_EMOJIS"] = "1";

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"'{spec.FileName}' did not start.");

        // A stop from outside — Blender terminating its job, Ctrl+C in a shell — must take the
        // child's whole tree down with this process: `dotnet watch` and `dotnet <dll>` both put
        // the game one level below the process this handle names, and a stop that left it
        // running would be no stop. Installed for the child's lifetime only, so outside it the
        // default handler still ends the process (an asset cook, say) at once.
        using var interrupted = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, interrupted.Token);
        using var onInterrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; interrupted.Cancel(); });
        using var onTerminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; interrupted.Cancel(); });
        using var killOnStop = linked.Token.Register(() => TryKill(process));
        started?.Invoke(process.Id);
        process.WaitForExit();
        return linked.IsCancellationRequested ? Interrupted : process.ExitCode;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone; the wait below observes that.
        }
    }
}

/// <summary>
/// The processes at the bottom of a tree: under <c>dotnet watch</c> that is the game itself, two
/// <c>dotnet</c> hosts below the one this process started. Killing only the leaves leaves
/// <c>dotnet watch</c> alive and parked on "waiting for a file to change", which is what makes a
/// restart without a rebuild possible.
/// </summary>
/// <remarks>Unix only, through <c>ps</c>: .NET exposes no parent-pid API, and the Windows way (a job object or WMI) is a different tool. On Windows nothing is killed and the caller's touch is a no-op for a running game.</remarks>
internal static class ProcessTree
{
    public static IReadOnlyList<int> Leaves(int root)
    {
        if (OperatingSystem.IsWindows()) return [];

        var children = new Dictionary<int, List<int>>();
        try
        {
            var ps = new ProcessStartInfo("ps", "-axo pid=,ppid=") { RedirectStandardOutput = true, UseShellExecute = false };
            using var process = Process.Start(ps);
            if (process is null) return [];
            while (process.StandardOutput.ReadLine() is { } line)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !int.TryParse(parts[0], out var pid) || !int.TryParse(parts[1], out var parent)) continue;
                if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
                list.Add(pid);
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return [];
        }

        var leaves = new List<int>();
        Collect(root, children, leaves);
        return leaves;
    }

    private static void Collect(int pid, Dictionary<int, List<int>> children, List<int> leaves)
    {
        if (!children.TryGetValue(pid, out var below) || below.Count == 0)
        {
            leaves.Add(pid);
            return;
        }

        foreach (var child in below) Collect(child, children, leaves);
    }

    public static void KillLeaves(int root)
    {
        foreach (var pid in Leaves(root))
        {
            if (pid == root) continue;
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Gone already.
            }
        }
    }
}
