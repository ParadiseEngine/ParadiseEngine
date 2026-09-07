#if PARADISE_PROFILING
using System;
using System.Collections.Generic;
using System.Linq;
using Paradise.Rendering.Pbr;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Sample;

/// <summary>Per-pass GPU timings and per-phase CPU timings averaged over a headless run, printed
/// as a table. The first frames are warm-up (pipelines, probe reset) and are not counted.</summary>
internal sealed class PassBenchmark
{
    private const int WarmUpFrames = 20;
    private readonly WebGpuRenderer _renderer;
    private readonly Dictionary<string, (double Total, int Frames)> _gpu = new(StringComparer.Ordinal);
    private double[] _cpu = new double[6];
    private double _gpuTotal;
    private double _frameTotal;
    private int _frames;
    private readonly System.Diagnostics.Stopwatch _frameClock = new();

    /// <summary>Call before the frame's RenderFrame; <see cref="Record"/> waits for the GPU, so the
    /// interval is the frame's GPU time from idle to idle plus its (sub-millisecond) CPU time —
    /// the number the per-pass timestamps cannot give on a GPU that overlaps passes.</summary>
    public void BeginFrame() => _frameClock.Restart();

    public PassBenchmark(WebGpuRenderer renderer)
    {
        _renderer = renderer;
        renderer.PassTimingEnabled = true;
        if (!renderer.SupportsPassTiming)
            Console.WriteLine("[bench] this adapter grants no timestamp queries; GPU timings will be empty.");
    }

    public void Record(int frame, PbrRenderer pbr)
    {
        var gpu = _renderer.ReadPassTimings();
        var frameMs = _frameClock.Elapsed.TotalMilliseconds;
        if (frame < WarmUpFrames) return;
        _frames++;
        _frameTotal += frameMs;
        var names = pbr.LastPassNames;
        for (var i = 0; i < gpu.Length && i < names.Count; i++)
        {
            var entry = _gpu.TryGetValue(names[i], out var e) ? e : default;
            _gpu[names[i]] = (entry.Total + gpu[i], entry.Frames + 1);
            _gpuTotal += gpu[i];
        }
        var cpu = pbr.LastCpuTimings;
        _cpu[0] += cpu.Partition;
        _cpu[1] += cpu.TraceBuild;
        _cpu[2] += cpu.Setup;
        _cpu[3] += cpu.Compile;
        _cpu[4] += cpu.Upload;
        _cpu[5] += cpu.Submit;
    }

    public void Report(int frameCount)
    {
        if (_frames == 0)
        {
            Console.WriteLine($"[bench] fewer than {WarmUpFrames} frames rendered; nothing to report.");
            return;
        }
        Console.WriteLine($"[bench] {_frames} timed frames of {frameCount}: {_frameTotal / _frames:F3} ms per frame, GPU idle to idle.");
        Console.WriteLine("[bench] GPU per pass (ms; on a GPU that overlaps passes these measure wall time, not exclusive cost):");
        foreach (var (name, (total, frames)) in _gpu.OrderByDescending(kv => kv.Value.Total / _frames))
            Console.WriteLine($"  {name,-28} {total / frames,8:F3} ms  ({frames} frames)");
        Console.WriteLine($"  {"GPU total",-28} {_gpuTotal / _frames,8:F3} ms");
        string[] phases = ["partition", "trace build", "setup", "compile", "upload", "submit"];
        Console.WriteLine("[bench] CPU per phase (ms):");
        for (var i = 0; i < phases.Length; i++)
            Console.WriteLine($"  {phases[i],-28} {_cpu[i] / _frames,8:F3} ms");
        Console.WriteLine($"  {"CPU total",-28} {_cpu.Sum() / _frames,8:F3} ms");
    }
}
#endif
