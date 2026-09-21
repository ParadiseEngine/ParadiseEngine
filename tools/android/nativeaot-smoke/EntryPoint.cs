using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Hosting.Android;
using Paradise.Rendering;
using Paradise.Rendering.WebGPU;
using Paradise.Rendering.Pbr;
using Paradise.Windowing;
using Paradise.Windowing.Sdl;

internal static class EntryPoint
{
    // This compile-only CI probe roots the real host, window and renderer rather than a hello-world stub.
    [UnmanagedCallersOnly(EntryPoint = "SDL_main", CallConvs = [typeof(CallConvCdecl)])]
    public static int Main(int argc, nint argv)
    {
        try
        {
            return SdlAndroidHost.Run(logger =>
            {
                using var platform = new SdlWindowPlatform(logger);
                using var window = platform.CreateWindow(new WindowOptions("NativeAOT compile probe", 64, 64));
                var surface = window.CreateSurface();
                using var renderer = new WebGpuRenderer(in surface, logger: logger);
                // Clear-only probes missed a platform-crypto dependency in texture identity.
                // Malformed KTX2 still exercises hashing, then takes the managed fallback.
                using var pbr = new PbrRenderer(renderer, new FeatureSwitches(), 64, 64);
                pbr.Materials.AddMaterial(new PbrMaterialDesc(), new PbrMaterialTextures
                {
                    BaseColor = new byte[] { 1, 2, 3 },
                });
                renderer.RenderClearFrame(new ColorRgba(0.1f, 0.2f, 0.3f, 1f));
            });
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
