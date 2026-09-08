using System.Diagnostics;
using TUnit.Core;

namespace Paradise.Rendering.WebGPU.Test;

public class PassTimingTests
{
    [Test]
    [NotInParallel]
    public async Task repeated_timing_readbacks_keep_native_handles_bounded()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("This regression measures Windows native handles.");
            return;
        }
        WebGpuRenderer backend;
        try
        {
            backend = WebGpuRenderer.CreateHeadless(16, 16);
        }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            Skip.Test(error.Message);
            return;
        }
        using var renderer = backend;
        if (!backend.SupportsPassTiming)
        {
            Skip.Test("Requires timestamp queries and -p:ParadiseProfiling=true.");
            return;
        }
        backend.PassTimingEnabled = true;
        void Frame()
        {
            backend.Submit(new ClearFrame(new ColorRgba(0, 0, 0, 1)).Record());
            var timings = backend.ReadPassTimings();
            if (timings.Length != 1 || !double.IsFinite(timings[0]) || timings[0] < 0)
                throw new InvalidOperationException("Missing or invalid clear-pass timing.");
        }
        for (var i = 0; i < 250; i++) Frame();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var before = process.HandleCount;
        for (var i = 0; i < 2000; i++) Frame();
        process.Refresh();
        await Assert.That(process.HandleCount - before).IsLessThan(128);
    }
}
