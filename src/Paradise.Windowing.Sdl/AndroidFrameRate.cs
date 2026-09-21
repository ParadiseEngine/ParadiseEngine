using System.Runtime.InteropServices;

namespace Paradise.Windowing.Sdl;

internal static unsafe class AndroidFrameRate
{
    internal static int? Request(nint window, float framesPerSecond)
    {
        if (window == 0) throw new ArgumentException("A live Android native window is required.", nameof(window));
        if (!float.IsFinite(framesPerSecond) || framesPerSecond < 0)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        // API 26 remains supported. Resolve the API-30 export only on Android rather than making
        // every APK depend on it, and never retain a function pointer after releasing its library.
        if (!NativeLibrary.TryLoad("libandroid.so", out var library)) return null;
        try
        {
            if (!NativeLibrary.TryGetExport(library, "ANativeWindow_setFrameRate", out var entry)) return null;
            var setFrameRate = (delegate* unmanaged[Cdecl]<nint, float, sbyte, int>)entry;
            return setFrameRate(window, framesPerSecond, 0); // FRAME_RATE_COMPATIBILITY_DEFAULT for games.
        }
        finally { NativeLibrary.Free(library); }
    }
}
