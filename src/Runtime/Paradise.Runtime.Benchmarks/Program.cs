using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Paradise.Features;
using Paradise.Hosting;
using Paradise.Runtime.Benchmarks;
using Paradise.Runtime.Test;
using Paradise.Windowing;
using Zio;
using Zio.FileSystems;

var options = BenchmarkOptions.Parse(args);
var ledger = new AllocationLedger();
var frame = ledger.Get("frame: CooperativeHost.Frame (total)");
using var sampler = options.SampleTypes ? new AllocationSampler() : null;
sampler?.Start();

using var content = new MemoryFileSystem();
content.CreateDirectory("/meshes");
content.WriteAllBytes(BenchmarkGame.CubePath, CubeMesh.Cooked());
using var game = new BenchmarkGame(options, content, ledger);
var switches = new FeatureSwitches(options.Features);
var hostOptions = new HostOptions
{
    Window = new WindowOptions("Paradise allocation benchmark", options.Width, options.Height),
    Headless = true,
    MaxCatchUp = options.Host == BenchmarkHost.Cooperative ? BenchmarkOptions.LockstepStep : new HostOptions().MaxCatchUp,
};
var window = new MeasurementWindow(ledger, sampler);

if (options.Host == BenchmarkHost.Cooperative) RunCooperative();
else RunThreaded();

Report.Print(options, window, ledger, sampler, game);
if (options.Budget is not { } budget) return 0;
if (options.Host == BenchmarkHost.Cooperative)
{
    if (frame.MaxBytes <= budget) return 0;
    Console.Error.WriteLine($"Over budget: a frame allocated {frame.MaxBytes} bytes ({frame.AllocatingCalls} of {frame.Calls} frames allocated), {budget} allowed.");
    return 2;
}

if (window.AllocatedBytes <= budget * window.Frames) return 0;
Console.Error.WriteLine($"Over budget: {window.AllocatedBytes} bytes over {window.Frames} frames, {budget} a frame allowed.");
return 2;

void RunCooperative()
{
    using var host = CooperativeHost.Start(game, new HeadlessWindowPlatform(), hostOptions, switches, NullLogger.Instance);
    var deadline = Stopwatch.StartNew();
    while (game.CompleteFrames < options.WarmupFrames)
    {
        if (!host.Frame()) throw new InvalidOperationException("The host stopped during warm-up.", host.Failure);
        if (deadline.Elapsed > TimeSpan.FromMinutes(2)) throw new TimeoutException($"Only {game.CompleteFrames} complete frames in two minutes.");
    }

    window.Begin();
    for (var i = 0; i < options.Frames; i++)
    {
        var start = AllocationLedger.Now();
        if (!host.Frame()) break;
        frame.Add(start);
    }

    window.End(options.Frames);
    if (host.Failure is { } failure) throw new InvalidOperationException("The host failed.", failure);
}

void RunThreaded()
{
    var last = options.WarmupFrames + options.Frames;
    game.CompleteFrame += complete =>
    {
        if (complete == options.WarmupFrames) window.Begin();
        else if (complete == last)
        {
            window.End(options.Frames);
            game.Window!.RequestClose();
        }
    };
    if (ParadiseHost.Run(game, new HeadlessWindowPlatform(), hostOptions, switches, NullLogger.Instance) != 0)
        throw new InvalidOperationException("The threaded host failed.");
    if (!window.Ended) throw new InvalidOperationException($"The run ended after {game.CompleteFrames} complete frames.");
}
