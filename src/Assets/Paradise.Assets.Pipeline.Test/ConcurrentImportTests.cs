using System.Text;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Jobs;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>Builds with <see cref="BuildRunner.Workers"/> must be indistinguishable from builds without.</summary>
public class ConcurrentImportTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    /// <summary>A thread-safe stand-in for ktx that holds each encode briefly, so encodes overlap.</summary>
    private sealed class SlowEncoder : ITextureEncoder
    {
        private int _running;
        private int _peak;
        private int _encodes;

        public int Peak => Volatile.Read(ref _peak);
        public int Encodes => Volatile.Read(ref _encodes);
        public string Identity => "slow-ktx 1.0";

        public string CacheKey(byte[] source, string sourceExtension, TexturePreset preset, TextureQuality quality)
            => ArtifactDigest.Compute(source, sourceExtension, preset.ToString(), quality.ToString(), Identity);

        public bool TryEncode(byte[] source, string sourceExtension, TexturePreset preset, TextureQuality quality, out byte[] ktx2, out string error)
        {
            var running = Interlocked.Increment(ref _running);
            int peak;
            while (running > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, running, peak) != peak) { }
            Thread.Sleep(20);
            Interlocked.Increment(ref _encodes);
            Interlocked.Decrement(ref _running);
            ktx2 = Encoding.UTF8.GetBytes($"ktx2:{preset}:{Convert.ToHexStringLower(source)}");
            error = "";
            return true;
        }
    }

    /// <summary>Records the most imports running at once; it does not opt into concurrency.</summary>
    private sealed class CountingImporter : IAssetImporter
    {
        private int _running;

        public int Peak { get; private set; }
        public string Name => "counted";
        public bool RecordsIdentity => true;
        public bool Claims(ImportCandidate candidate) => candidate.HasExtension(".counted");

        public bool Import(ImportContext context, List<string> errors)
        {
            var running = Interlocked.Increment(ref _running);
            Peak = Math.Max(Peak, running);
            Thread.Sleep(5);
            context.Output.WriteAllBytes("/" + context.Source, context.FileSystem.ReadAllBytes(context.Asset));
            Interlocked.Decrement(ref _running);
            return true;
        }
    }

    [Test]
    public async Task a_build_with_workers_matches_a_build_without()
    {
        // One project, cloned: sidecars mint fresh guids, which would differ between two projects.
        using var project = Project();
        var (sequentialManifest, sequentialFiles) = Build(project.Clone(), workers: null, out _);
        using var pool = new JobWorkerPool(3);
        var (concurrentManifest, concurrentFiles) = Build(project.Clone(), pool, out var encoder);

        await Assert.That(concurrentManifest).IsEqualTo(sequentialManifest);
        await Assert.That(concurrentFiles.Keys.Order(StringComparer.Ordinal)).IsEquivalentTo(sequentialFiles.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in sequentialFiles)
            await Assert.That(concurrentFiles[path].SequenceEqual(bytes)).IsTrue();
        await Assert.That(encoder.Encodes).IsEqualTo(12);
        // The textures really overlapped: the point of the workers.
        await Assert.That(encoder.Peak).IsGreaterThan(1);
    }

    [Test]
    public async Task importers_that_do_not_opt_in_never_run_concurrently()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var counted = new CountingImporter();
        IAssetImporter[] chain = [.. AssetImporters.All, counted];
        for (var i = 0; i < 12; i++)
            ProjectVerifierTests.AddAssetWithSidecar(fileSystem, $"/game/assets/data/item{i:D2}.counted", chain);
        using var pool = new JobWorkerPool(3);

        var result = new BuildRunner(fileSystem, s_layout, new SlowEncoder(), importers: chain) { Workers = pool }.Run();

        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.AssetCount).IsEqualTo(12);
        await Assert.That(counted.Peak).IsEqualTo(1);
    }

    [Test]
    public async Task a_rebuild_with_workers_reuses_everything_unchanged()
    {
        using var fileSystem = Project();
        var encoder = new SlowEncoder();
        using var pool = new JobWorkerPool(3);
        var runner = new BuildRunner(fileSystem, s_layout, encoder) { Workers = pool };

        await Assert.That(runner.Run().Succeeded).IsTrue();
        var manifest = fileSystem.ReadAllText("/game/build/manifest.json");
        await Assert.That(runner.Run().Succeeded).IsTrue();

        await Assert.That(encoder.Encodes).IsEqualTo(12);
        await Assert.That(fileSystem.ReadAllText("/game/build/manifest.json")).IsEqualTo(manifest);
    }

    private static (string Manifest, Dictionary<string, byte[]> Files) Build(Zio.FileSystems.MemoryFileSystem project, JobWorkerPool? workers, out SlowEncoder encoder)
    {
        using var fileSystem = project;
        encoder = new SlowEncoder();
        var result = new BuildRunner(fileSystem, s_layout, encoder) { Workers = workers }.Run();
        if (!result.Succeeded) throw new InvalidOperationException(string.Join("\n", result.Errors));

        var files = fileSystem.EnumerateFiles("/game/build", "*", SearchOption.AllDirectories)
            .Where(path => path.GetName() != BuildManifest.FileName && !path.GetName().StartsWith('.'))
            .ToDictionary(path => path.FullName, path => fileSystem.ReadAllBytes(path), StringComparer.Ordinal);
        return (fileSystem.ReadAllText("/game/build/manifest.json"), files);
    }

    private static Zio.FileSystems.MemoryFileSystem Project()
    {
        var fileSystem = ProjectVerifierTests.CreateProject();
        for (var i = 0; i < 12; i++)
        {
            // Distinct bytes: identical sources share one artifact-cache entry and would hide encodes.
            ProjectVerifierTests.AddAssetWithSidecar(fileSystem, $"/game/assets/textures/t{i:D2}.png");
            fileSystem.WriteAllBytes($"/game/assets/textures/t{i:D2}.png", [1, 2, 3, (byte)i]);
        }

        return fileSystem;
    }
}
