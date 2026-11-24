using System.Diagnostics;

namespace Paradise.Hosting;

/// <summary>A worker thread whose join also covers the end of its operating-system thread.</summary>
/// <remarks><see cref="Thread.Join(TimeSpan)"/> returns once the managed thread has finished, while
/// its operating-system thread still runs the thread-local destructors that native libraries
/// registered on it. A process that exits in that window can unload such a library first: WSL's
/// Direct3D 12 loader unloads its core library from an exit handler, and the presentation thread
/// then faults in a destructor that is no longer mapped. Linux lists each live thread under /proc,
/// so the join waits for that entry to go; on other systems it is the managed join alone.</remarks>
internal sealed class HostWorker
{
    private readonly Thread _thread;
    private volatile string? _taskDirectory;

    internal HostWorker(string name, Action body)
    {
        _thread = new Thread(() =>
        {
            _taskDirectory = CurrentTaskDirectory();
            body();
        })
        {
            IsBackground = true,
            Name = name,
        };
    }

    internal void Start() => _thread.Start();

    internal bool Join(TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        if (!_thread.Join(timeout))
        {
            return false;
        }

        if (_taskDirectory is not { } taskDirectory)
        {
            return true;
        }

        var remaining = timeout - Stopwatch.GetElapsedTime(started);
        return SpinWait.SpinUntil(() => !Directory.Exists(taskDirectory), remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
    }

    private static string? CurrentTaskDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            return Directory.ResolveLinkTarget("/proc/thread-self", returnFinalTarget: false)?.FullName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
