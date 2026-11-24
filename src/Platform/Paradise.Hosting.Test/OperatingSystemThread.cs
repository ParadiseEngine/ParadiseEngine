using System.Runtime.InteropServices;

namespace Paradise.Hosting.Test;

/// <summary>Stands in for a native library that leaves work for the end of the calling thread.</summary>
internal static partial class OperatingSystemThread
{
    /// <summary>The calling thread's /proc entry, which exists for as long as the thread does.</summary>
    internal static string CurrentTaskDirectory() =>
        Directory.ResolveLinkTarget("/proc/thread-self", returnFinalTarget: false)!.FullName;

    /// <summary>Registers a thread-local destructor that sleeps while the calling thread ends.</summary>
    /// <remarks>The destructor is libc's own <c>usleep</c>, called with the stored value, so the
    /// thread never re-enters managed code after the runtime has let go of it.</remarks>
    internal static void DelayCurrentThreadExit(TimeSpan delay)
    {
        var usleep = NativeLibrary.GetExport(NativeLibrary.GetMainProgramHandle(), "usleep");
        var microseconds = (nint)delay.TotalMicroseconds;
        if (CreateKey(out var key, usleep) != 0 || SetSpecific(key, microseconds) != 0)
        {
            throw new InvalidOperationException("The thread-local destructor could not be registered.");
        }
    }

    [LibraryImport("libc", EntryPoint = "pthread_key_create")]
    private static partial int CreateKey(out uint key, nint destructor);

    [LibraryImport("libc", EntryPoint = "pthread_setspecific")]
    private static partial int SetSpecific(uint key, nint value);
}
