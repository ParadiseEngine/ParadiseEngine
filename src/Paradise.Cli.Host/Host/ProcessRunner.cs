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
        try
        {
            started?.Invoke(process.Id);
        }
        catch
        {
            // A caller that could not take the pid has no way to stop what it started.
            TryKill(process);
            process.WaitForExit(5_000);
            throw;
        }

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
/// <remarks>.NET exposes no parent-pid API: <c>ps</c> on Unix, a Toolhelp snapshot on Windows.</remarks>
internal static class ProcessTree
{
    public sealed record Leaf(int Pid, string Command)
    {
        /// <summary>Under <c>dotnet watch</c> every process but the game is a <c>dotnet</c> host (the watch, its MSBuild nodes, <c>dotnet run</c>); the game is the one apphost.</summary>
        public bool IsGame => !string.Equals(Path.GetFileNameWithoutExtension(Command), "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the game itself is up under <paramref name="root"/>; false while <c>dotnet watch</c> is still building or loading.</summary>
    public static bool HasGameLeaf(int root) => Leaves(root).Any(leaf => leaf.IsGame);

    public static IReadOnlyList<Leaf> Leaves(int root)
    {
        var children = OperatingSystem.IsWindows() ? WindowsChildren() : UnixChildren();
        if (children is null) return [];

        var leaves = new List<Leaf>();
        Collect(new Leaf(root, "dotnet"), children, leaves);
        return leaves;
    }

    private static Dictionary<int, List<Leaf>>? UnixChildren()
    {
        var children = new Dictionary<int, List<Leaf>>();
        try
        {
            var ps = new ProcessStartInfo("ps", "-axo pid=,ppid=,comm=") { RedirectStandardOutput = true, UseShellExecute = false };
            using var process = Process.Start(ps);
            if (process is null) return [];
            while (process.StandardOutput.ReadLine() is { } line)
            {
                var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || !int.TryParse(parts[0], out var pid) || !int.TryParse(parts[1], out var parent)) continue;
                if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
                list.Add(new Leaf(pid, parts[2].Trim()));
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }

        return children;
    }

    /// <summary>Every process's parent from one Toolhelp snapshot; the Windows counterpart of <c>ps -o pid,ppid,comm</c>.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Dictionary<int, List<Leaf>>? WindowsChildren()
    {
        var snapshot = Toolhelp.CreateToolhelp32Snapshot(Toolhelp.Th32csSnapProcess, 0);
        if (snapshot == Toolhelp.InvalidHandle) return null;

        var children = new Dictionary<int, List<Leaf>>();
        try
        {
            var entry = new Toolhelp.ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<Toolhelp.ProcessEntry32>() };
            if (!Toolhelp.Process32FirstW(snapshot, ref entry)) return null;
            do
            {
                var parent = (int)entry.th32ParentProcessID;
                if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
                list.Add(new Leaf((int)entry.th32ProcessID, entry.szExeFile));
            }
            while (Toolhelp.Process32NextW(snapshot, ref entry));
        }
        finally
        {
            Toolhelp.CloseHandle(snapshot);
        }

        return children;
    }

    private static class Toolhelp
    {
        public const uint Th32csSnapProcess = 0x2;
        public static readonly nint InvalidHandle = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ProcessEntry32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public nuint th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        // DllImport rather than LibraryImport: the struct's inline string is not a shape the
        // source generator marshals, and this is three calls on a menu click.
#pragma warning disable SYSLIB1054
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32FirstW(nint hSnapshot, ref ProcessEntry32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32NextW(nint hSnapshot, ref ProcessEntry32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint hObject);
#pragma warning restore SYSLIB1054
    }

    private static void Collect(Leaf node, Dictionary<int, List<Leaf>> children, List<Leaf> leaves)
    {
        if (!children.TryGetValue(node.Pid, out var below) || below.Count == 0)
        {
            leaves.Add(node);
            return;
        }

        foreach (var child in below) Collect(child, children, leaves);
    }

    public static void KillLeaves(int root)
    {
        foreach (var (pid, _) in Leaves(root))
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
