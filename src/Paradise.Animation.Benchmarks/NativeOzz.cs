using System.Reflection;
using System.Runtime.InteropServices;

namespace Paradise.Animation.Benchmarks;

/// <summary>
/// ozz-animation's own C++ sampler behind the twelve-function shim the spike built
/// (<c>.spike/shim/ParadiseOzz.cpp</c>: SamplingJob + LocalToModelJob, 16 floats per joint).
/// The engine ships no native ozz, so the library is found only through
/// <c>PARADISE_OZZ_NATIVE</c> — the path to <c>libParadiseOzz.dylib</c>/<c>.so</c>/<c>.dll</c> — and
/// the native rows are absent from a run without it.
/// </summary>
internal static unsafe partial class NativeOzz
{
    public const string EnvironmentVariable = "PARADISE_OZZ_NATIVE";

    private const string Lib = "ParadiseOzz";

    public static bool IsAvailable { get; } = Probe();

    [LibraryImport(Lib, EntryPoint = "Pdx_Ozz_LoadSkeleton")] public static partial nint LoadSkeleton(byte* bytes, nuint length);
    [LibraryImport(Lib, EntryPoint = "Pdx_Ozz_LoadAnimation")] public static partial nint LoadAnimation(byte* bytes, nuint length);
    [LibraryImport(Lib, EntryPoint = "Pdx_Ozz_FreeSkeleton")] public static partial void FreeSkeleton(nint skeleton);
    [LibraryImport(Lib, EntryPoint = "Pdx_Ozz_FreeAnimation")] public static partial void FreeAnimation(nint animation);
    [LibraryImport(Lib, EntryPoint = "Pdx_Ozz_TrackCount")] public static partial int TrackCount(nint animation);
    [LibraryImport(Lib, EntryPoint = "Pdx_Ozz_CreateContext")] public static partial nint CreateContext(int maxTracks);
    [LibraryImport(Lib, EntryPoint = "Pdx_Ozz_FreeContext")] public static partial void FreeContext(nint context);
    [LibraryImport(Lib, EntryPoint = "Pdx_Ozz_SampleModelSpace")] public static partial int SampleModelSpace(nint skeleton, nint animation, nint context, float ratio, float* outMatrices);

    private static bool Probe()
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        NativeLibrary.SetDllImportResolver(typeof(NativeOzz).Assembly, (name, assembly, search) =>
            name == Lib ? NativeLibrary.Load(path) : IntPtr.Zero);
        try
        {
            return NativeLibrary.TryLoad(path, out _);
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }
}
