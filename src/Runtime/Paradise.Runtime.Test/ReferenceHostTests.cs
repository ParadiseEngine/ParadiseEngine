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
        await Assert.That(backend.LastCommands.Count(command => command.Kind == RenderCommandKind.DrawIndexed)).IsGreaterThanOrEqualTo(game.Entities);
    }

    [Test]
    public async Task the_reference_game_draws_its_scene_on_a_gpu()
    {
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
        // Lit cubes cover part of the frame and leave the rest to the background.
        var lit = 0;
        for (var i = 0; i < pixels!.Length; i += 4)
        {
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 60) lit++;
        }

        var total = pixels.Length / 4;
        await Assert.That(lit).IsGreaterThan(total / 20);
        await Assert.That(lit).IsLessThan(total);
    }
}
