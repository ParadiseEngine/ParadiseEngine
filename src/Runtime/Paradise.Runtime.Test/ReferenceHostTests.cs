using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Paradise.Features;
using Paradise.Hosting;
using Paradise.Rendering;
using Paradise.Rendering.WebGPU;
using Paradise.Windowing;
using Zio;
using Zio.FileSystems;

namespace Paradise.Runtime.Test;

/// <summary>Runs the reference game end to end, headless: simulation, snapshots, asset loading, scene
/// extraction and PBR rendering composed the way a downstream game composes them.</summary>
[NotInParallel]
public class ReferenceHostTests
{
    private static MemoryFileSystem Content()
    {
        var content = new MemoryFileSystem();
        content.CreateDirectory("/meshes");
        content.WriteAllBytes(ReferenceGame.CubePath, CubeMesh.Cooked());
        return content;
    }

    /// <summary>Drives frames until <paramref name="completeFrames"/> drew every entity.</summary>
    private static void Run(ReferenceGame game, int completeFrames)
    {
        var options = new HostOptions { Window = new WindowOptions("Paradise reference host", 96, 64), Headless = true };
        using var host = CooperativeHost.Start(game, new HeadlessWindowPlatform(), options, new FeatureSwitches(), NullLogger.Instance);
        var clock = Stopwatch.StartNew();
        while (host.Frame())
        {
            if (game.CompleteFrames >= completeFrames || clock.Elapsed > TimeSpan.FromSeconds(60)) host.RequestStop();
        }

        if (host.Failure is { } failure) throw new InvalidOperationException("The reference host failed.", failure);
    }

    [Test]
    public async Task the_reference_game_composes_on_a_recording_backend()
    {
        using var content = Content();
        using var game = new ReferenceGame(content, _ => new RecordingBackend());

        Run(game, completeFrames: 5);

        await Assert.That(game.CompleteFrames).IsGreaterThanOrEqualTo(5);
        await Assert.That(game.LastInstanceCount).IsEqualTo(game.Entities);
        // Extraction carried the snapshot's placements over unchanged...
        await Assert.That(game.LastInstances.Select(instance => instance.Model)).IsEquivalentTo(game.LastSnapshotModels);
        // ...into one instance per entity, kept across frames.
        await Assert.That(game.SeenInstances.Count).IsEqualTo(game.Entities);
        var backend = (RecordingBackend)game.Backend!;
        await Assert.That(backend.Submits).IsGreaterThanOrEqualTo(5);
        // Automatic instancing may merge the identical cubes into one draw; the instances still all reach the stream.
        var drawn = backend.LastCommands
            .Where(command => command.Kind == RenderCommandKind.DrawIndexed)
            .Max(command => (long)command.DrawIndexed.InstanceCount);
        var instances = backend.LastCommands
            .Where(command => command.Kind == RenderCommandKind.DrawIndexed)
            .Sum(command => (long)command.DrawIndexed.InstanceCount);
        await Assert.That(drawn).IsGreaterThanOrEqualTo(1L);
        await Assert.That(instances).IsGreaterThanOrEqualTo(game.Entities);
    }

    [Test]
    public async Task the_reference_game_draws_its_scene_on_a_gpu()
    {
        // A lane decision, not a missing GPU: lavapipe keeps PBR's atlases in system RAM, and the lane that
        // sets this runs PBR on another adapter.
        if (Environment.GetEnvironmentVariable("PARADISE_SKIP_PBR_GPU") == "1")
        {
            Skip.Test("PBR GPU rendering runs on another CI lane (PARADISE_SKIP_PBR_GPU).");
            return;
        }

        using var content = Content();
        byte[]? pixels = null;
        using var game = new ReferenceGame(
            content,
            surface => new WebGpuRenderer(in surface),
            afterCompleteFrame: backend => pixels = ((WebGpuRenderer)backend).ReadbackColor(out _, out _));

        try
        {
            Run(game, completeFrames: 3);
        }
        catch (Exception error) when (error is AdapterUnavailableException or DllNotFoundException)
        {
            GpuRequirement.Skip($"No WebGPU adapter available: {error.Message}");
            return;
        }

        await Assert.That(game.CompleteFrames).IsGreaterThanOrEqualTo(3);
        await Assert.That(pixels).IsNotNull();
        // The cubes cover part of the frame and leave the rest to the background, which the corner shows:
        // the 3 x 3 grid spans about 4.2 units at 6 units from a 60-degree camera, well inside the frame.
        var background = (pixels![0], pixels[1], pixels[2]);
        var covered = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var difference = Math.Abs(pixels[i] - background.Item1) + Math.Abs(pixels[i + 1] - background.Item2) + Math.Abs(pixels[i + 2] - background.Item3);
            if (difference > 24) covered++;
        }

        var total = pixels.Length / 4;
        await Assert.That(covered).IsGreaterThan(total / 20);
        await Assert.That(covered).IsLessThan(total / 2);
    }
}
