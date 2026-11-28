using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;

namespace Paradise.Runtime.Benchmarks;

/// <summary>Prints a run's results as Markdown.</summary>
internal static class Report
{
    private const string RenderTotal = "present: PbrRenderer.RenderFrame (total)";
    private const string FrameTotal = "frame: CooperativeHost.Frame (total)";

    public static void Print(BenchmarkOptions options, MeasurementWindow window, AllocationLedger ledger, AllocationSampler? sampler, BenchmarkGame game)
    {
        var text = new StringBuilder();
        var frames = Math.Max(1, window.Frames);
        var seconds = window.Elapsed.TotalSeconds;
        Line(text, $"# Allocation benchmark: {options.Workload} workload, {options.Backend} backend, {options.Host} host");
        Line(text, $"");
        Line(text, $"- {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}; GC: {(GCSettings.IsServerGC ? "server" : "workstation")}, {GCSettings.LatencyMode}");
        Line(text, $"- {options.Entities} ECS entities, {game.LastInstanceCount} scene instances, {options.Width}x{options.Height}, warm-up {options.WarmupFrames} frames; probes {(options.ProbeFeatures ? "on" : "off")}, type sampling {(sampler is null ? "off" : "on")}");
        Line(text, $"- {window.Frames} measured frames in {seconds:F2} s ({window.Frames / Math.Max(seconds, 1e-9):F0} frames/s)");
        if (game.Renderer is { } renderer)
        {
            var passes = renderer.LastPassNames;
            var commands = game.Backend is NullRenderer recorded ? string.Create(CultureInfo.InvariantCulture, $", {recorded.LastCommandCount} commands") : "";
            Line(text, $"- Last frame: {passes.Count} live passes{commands}: {string.Join(", ", passes)}");
        }

        Line(text, $"");
        Line(text, $"| Process (all threads) | Value |");
        Line(text, $"| --- | ---: |");
        Line(text, $"| Allocated | {Bytes(window.AllocatedBytes)} |");
        Line(text, $"| Allocated per frame | {Bytes(window.AllocatedBytes / frames)} |");
        Line(text, $"| At 60 frames/s | {Bytes(window.AllocatedBytes / frames * 60)}/s |");
        Line(text, $"| Collections gen0 / gen1 / gen2 | {window.Gen0} / {window.Gen1} / {window.Gen2} |");
        Line(text, $"| Frames per gen0 collection | {(window.Gen0 == 0 ? "none collected" : (window.Frames / (double)window.Gen0).ToString("F0", CultureInfo.InvariantCulture))} |");
        Line(text, $"| GC pause total | {window.Pause.TotalMilliseconds:F2} ms |");
        Line(text, $"| Working set: start / end / after forced GC | {Bytes(window.WorkingSetAtStart)} / {Bytes(window.WorkingSetAtEnd)} / {Bytes(window.WorkingSetAfterCollection)} |");
        if (options.Backend == BenchmarkBackend.WebGpu)
        {
            var (start, end, after) = (window.HandlesAtStart, window.HandlesAtEnd, window.HandlesAfterCollection);
            Line(text, $"| Live WebGPU handles: start / end / after forced GC | {start.Total} / {end.Total} / {after.Total} |");
            Line(text, $"| … texture views | {start.TextureViews} / {end.TextureViews} / {after.TextureViews} |");
            Line(text, $"| … textures | {start.Textures} / {end.Textures} / {after.Textures} |");
        }

        Line(text, $"");

        PrintPhases(text, ledger, frames);
        if (sampler is not null) PrintTypes(text, sampler, options.TopTypes);
        Console.Write(text.ToString());
    }

    private static void PrintPhases(StringBuilder text, AllocationLedger ledger, int frames)
    {
        Line(text, $"Thread-local bytes per phase (exact). Bytes/frame divides by measured frames; a phase on the threaded host's simulation thread may run more or fewer times than frames.");
        Line(text, $"");
        Line(text, $"| Phase | Bytes/frame | Calls | Allocating calls | Max bytes/call |");
        Line(text, $"| --- | ---: | ---: | ---: | ---: |");
        var silent = new List<string>();
        foreach (var phase in ledger.Phases)
        {
            if (phase.Calls == 0) continue;
            if (phase.Bytes == 0)
            {
                silent.Add(phase.Name);
                continue;
            }

            Line(text, $"| {phase.Name} | {phase.Bytes / (double)frames:F0} | {phase.Calls} | {phase.AllocatingCalls} | {phase.MaxBytes} |");
        }

        var byName = ledger.Phases.ToDictionary(phase => phase.Name, StringComparer.Ordinal);
        if (byName.TryGetValue(FrameTotal, out var frame) && frame.Calls > 0)
        {
            var inside = ledger.Phases.Where(phase => phase.Name.StartsWith("sim:", StringComparison.Ordinal) || phase.Name.StartsWith("present:", StringComparison.Ordinal)).Sum(phase => phase.Bytes);
            Line(text, $"| (host outside game callbacks) | {(frame.Bytes - inside) / (double)frames:F0} | | | |");
        }

        if (byName.TryGetValue(RenderTotal, out var render) && render.Calls > 0)
        {
            var probed = ledger.Phases.Where(IsRendererSegment).Sum(phase => phase.Bytes);
            if (probed > 0 || ledger.Phases.Any(phase => IsRendererSegment(phase) && phase.Calls > 0))
                Line(text, $"| (RenderFrame not attributed to a segment) | {(render.Bytes - probed) / (double)frames:F0} | | | |");
        }

        Line(text, $"");
        if (silent.Count > 0)
        {
            Line(text, $"Allocation-free phases ({silent.Count}): {string.Join(", ", silent)}.");
            Line(text, $"");
        }
    }

    private static bool IsRendererSegment(AllocationLedger.Phase phase) =>
        phase.Name.StartsWith("render:", StringComparison.Ordinal) || phase.Name.StartsWith("prepare:", StringComparison.Ordinal)
        || phase.Name.StartsWith("setup:", StringComparison.Ordinal) || phase.Name.StartsWith("beforeSubmit:", StringComparison.Ordinal);

    private static void PrintTypes(StringBuilder text, AllocationSampler sampler, int top)
    {
        var types = sampler.Snapshot();
        var ticks = Math.Max(1, types.Sum(type => type.Ticks));
        Line(text, $"Sampled allocation types (GCAllocationTick, one sample per ~100 KB per thread; {ticks} samples):");
        Line(text, $"");
        Line(text, $"| Type | Samples | Share |");
        Line(text, $"| --- | ---: | ---: |");
        foreach (var (type, count, _) in types.Take(top))
            Line(text, $"| `{type}` | {count} | {count * 100.0 / ticks:F1}% |");
        Line(text, $"");
    }

    private static string Bytes(long bytes) => bytes switch
    {
        >= 10L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1 << 20):F1} MiB"),
        >= 10L << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F1} KiB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
    };

    private static void Line(StringBuilder text, FormattableString line) => text.AppendLine(line.ToString(CultureInfo.InvariantCulture));
}
