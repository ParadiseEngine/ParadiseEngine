using System;
using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace Paradise.Rendering.Browser.Sample;

/// <summary>Acceptance harness for <see cref="BrowserRenderer"/>: renders one of two scenes into a
/// canvas, driven by the page's <c>requestAnimationFrame</c> (never a timer — a timer would hide
/// exactly the stalls a browser backend is judged on), and writes a machine-readable marker into
/// the DOM so a headless driver can wait on a real outcome rather than a fixed sleep.</summary>
/// <remarks>
/// <para><c>?scene=cube</c> (default) runs the engine sample's lit cube; <c>?scene=pbr</c> runs the
/// PBR procedural scene with a shadow-casting directional light.</para>
/// <para>There is no <c>Main</c> body: the page calls <see cref="InitAsync"/> and then pumps
/// <see cref="OnAnimationFrame"/>, so the runtime is driven entirely through exports and never has
/// to keep a managed loop alive.</para>
/// </remarks>
[SupportedOSPlatform("browser")]
public static partial class Program
{
    private const string HostModule = "paradise-sample-host";
    /// <summary>Frames that must land without a GPU error before the page reports success.</summary>
    /// <remarks>This was 60, which was long enough for WebGPU's asynchronous validation errors to
    /// arrive but NOT long enough to survive the wasm interpreter's tier-up: a 31 KB stack local in
    /// <c>PbrRenderer.UploadFrameUniforms</c> aborted the whole runtime at around frame 110, and
    /// this page cheerfully reported SAMPLE-OK at frame 60 first. A marker that fires before a known
    /// failure mode is worse than no marker, so the bar is now high enough that every hot method has
    /// been tiered up before the page claims success.</remarks>
    private const int FramesForSuccess = 300;

    private static BrowserRenderer? s_renderer;
    private static LitCubeScene? s_cubeScene;
    private static PbrShadowScene? s_pbrScene;
    private static string s_sceneName = "cube";
    private static int s_frames;
    private static bool s_reported;
    private static bool s_failed;
    private static readonly Stopwatch s_clock = Stopwatch.StartNew();
    private static double s_startMs;
    private static double s_windowStartMs;
    private static double s_cpuMsInWindow;
    private static double s_cpuMsTotal;
    private static int s_framesInWindow;
    private static double s_lastFps;
    private static double s_lastCpuMs;

    /// <summary>Never invoked — the WebAssembly SDK requires an entry point, but this app is driven
    /// entirely through its <c>[JSExport]</c> surface (see the type remarks).</summary>
    public static void Main()
    {
    }

    /// <summary>Create the renderer and the requested scene. Resolves once the first frame can be
    /// drawn; rejects (and writes SAMPLE-FAIL) if WebGPU is unavailable or setup throws.</summary>
    [JSExport]
    internal static async Task InitAsync(string scene, string hostModuleUrl, int width, int height, int extraBoxes)
    {
        try
        {
            // Absolute, resolved page-side: JSHost.ImportAsync dynamic-imports from inside
            // _framework/, so a page-relative path would look for the module there.
            await JSHost.ImportAsync(HostModule, hostModuleUrl).ConfigureAwait(false);
            s_sceneName = string.IsNullOrEmpty(scene) ? "cube" : scene;

            s_renderer = await BrowserRenderer.CreateAsync("#gpu-canvas", (uint)width, (uint)height).ConfigureAwait(false);
            Console.WriteLine($"[sample] adapter: {s_renderer.AdapterInfo}");
            Console.WriteLine($"[sample] color format {s_renderer.ColorFormat}, uniform alignment {s_renderer.UniformBufferOffsetAlignment}, BC compression {s_renderer.SupportsBcTextureCompression}");

            switch (s_sceneName)
            {
                case "cube":
                    s_cubeScene = new LitCubeScene(s_renderer, (uint)width, (uint)height);
                    break;
                case "pbr":
                    s_pbrScene = new PbrShadowScene(s_renderer, (uint)width, (uint)height, extraBoxes);
                    break;
                default:
                    throw new ArgumentException($"Unknown scene '{s_sceneName}' — expected 'cube' or 'pbr'.", nameof(scene));
            }
            SetStatusJs($"running scene={s_sceneName} adapter={s_renderer.AdapterInfo}");
            s_startMs = s_clock.Elapsed.TotalMilliseconds;
            s_windowStartMs = s_startMs;
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
    }

    /// <summary>One animation frame. Called from the page's rAF pump; swallows nothing — the first
    /// exception latches the failure marker and stops further rendering.</summary>
    [JSExport]
    internal static void OnAnimationFrame()
    {
        if (s_failed || s_renderer is null) return;
        try
        {
            var frameStartMs = s_clock.Elapsed.TotalMilliseconds;
            s_cubeScene?.RenderFrame();
            s_pbrScene?.RenderFrame();
            var nowMs = s_clock.Elapsed.TotalMilliseconds;
            s_frames++;
            s_framesInWindow++;
            s_cpuMsInWindow += nowMs - frameStartMs;
            s_cpuMsTotal += nowMs - frameStartMs;

            // WebGPU reports validation failures asynchronously, so a frame that "succeeded" can
            // still have produced nothing; polling turns that into a visible failure instead of a
            // page that animates a clear colour forever.
            var error = s_renderer.TakeGpuError();
            if (error.Length > 0) throw new InvalidOperationException($"WebGPU error: {error}");

            var windowMs = nowMs - s_windowStartMs;
            if (windowMs >= 1000.0)
            {
                s_lastFps = s_framesInWindow * 1000.0 / windowMs;
                s_lastCpuMs = s_cpuMsInWindow / s_framesInWindow;
                s_windowStartMs = nowMs;
                s_framesInWindow = 0;
                s_cpuMsInWindow = 0.0;
                SetStatsJs($"scene {s_sceneName} | frame {s_frames} | {s_lastFps:F1} fps | {s_lastCpuMs:F2} ms cpu/frame");
            }

            if (!s_reported && s_frames >= FramesForSuccess)
            {
                s_reported = true;
                // Averages over the whole run, not the last rolling window: the marker has to
                // carry a real number even when the run is barely longer than one window.
                var elapsedMs = nowMs - s_startMs;
                SetStatusJs(
                    $"SAMPLE-OK scene={s_sceneName} frames={s_frames} fps={s_frames * 1000.0 / elapsedMs:F1} " +
                    $"cpuMs={s_cpuMsTotal / s_frames:F2} format={s_renderer.ColorFormat} adapter={s_renderer.AdapterInfo}");
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private static void Fail(Exception ex)
    {
        s_failed = true;
        Console.Error.WriteLine(ex);
        try
        {
            SetStatusJs($"SAMPLE-FAIL: {ex.GetType().Name}: {ex.Message}");
        }
        catch (JSException)
        {
            // The host module never loaded (the failure happened during ImportAsync); the JS side
            // reports its own rejection in that case.
        }
    }

    [JSImport("setStatus", HostModule)]
    private static partial void SetStatusJs(string text);

    [JSImport("setStats", HostModule)]
    private static partial void SetStatsJs(string text);
}
