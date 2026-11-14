using System.Text;
using Microsoft.Coyote.Specifications;
using Zio;
using Zio.FileSystems;

namespace Paradise.Runtime.CoyoteTest;

/// <summary>The loader's one cross-thread edge: workers decode and hand results back through a concurrent
/// queue while the owning thread loads, releases and updates.</summary>
public static class AssetLoaderConcurrencyTests
{
    private sealed class Decoded(string text) : IDisposable
    {
        public string Text { get; } = text;
        public int Disposals;
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    private sealed class Resident(string text)
    {
        public string Text { get; } = text;
    }

    private sealed class TextType : IAssetType<Decoded, Resident>
    {
        public readonly List<Decoded> Decodes = [];
        public int Uploads;
        public int Unloads;

        public Decoded Decode(UPath path, byte[] bytes)
        {
            var decoded = new Decoded(Encoding.UTF8.GetString(bytes));
            lock (Decodes) Decodes.Add(decoded);
            return decoded;
        }

        public Resident Upload(Decoded decoded)
        {
            Uploads++;
            return new Resident(decoded.Text);
        }

        public void Unload(Resident resident) => Unloads++;
    }

    private static MemoryFileSystem Content(int files)
    {
        var fs = new MemoryFileSystem();
        for (var i = 0; i < files; i++) fs.WriteAllText($"/{i}.txt", $"asset {i}");
        return fs;
    }

    /// <summary>Owner-thread updates race worker completions: every load becomes ready exactly once with
    /// its own bytes, and the budget is never exceeded.</summary>
    public static async Task UpdatesRacingDecodes_UploadEachAssetOnceWithinBudget()
    {
        using var content = Content(3);
        var type = new TextType();
        using var loader = new AssetLoader(content, new AssetLoaderOptions { MaxUploadsPerUpdate = 1 });
        var handles = new[] { loader.Load("/0.txt", type), loader.Load("/1.txt", type), loader.Load("/2.txt", type) };

        while (loader.Pending > 0)
        {
            var uploaded = loader.Update();
            Specification.Assert(uploaded <= 1, $"An update uploaded {uploaded} assets with a budget of one.");
            await Task.Yield();
        }

        for (var i = 0; i < handles.Length; i++)
        {
            Specification.Assert(handles[i].TryGet(out var value) && value.Text == $"asset {i}",
                $"Asset {i} did not become ready with its own content.");
        }

        Specification.Assert(type.Uploads == 3, $"{type.Uploads} uploads for three assets.");
    }

    /// <summary>A release racing the worker's decode discards the result exactly once and never uploads it,
    /// whichever finishes first.</summary>
    public static async Task ReleaseRacingDecode_DiscardsWithoutUploading()
    {
        using var content = Content(1);
        var type = new TextType();
        using var loader = new AssetLoader(content);
        var handle = loader.Load("/0.txt", type);
        await Task.Yield();
        loader.Release(handle);

        while (loader.InFlight > 0)
        {
            loader.Update();
            await Task.Yield();
        }

        loader.Update();
        Specification.Assert(type.Uploads == 0, "A released asset was uploaded.");
        Specification.Assert(handle.State == AssetState.Released, $"A released asset ended {handle.State}.");
        lock (type.Decodes)
        {
            Specification.Assert(type.Decodes.All(decoded => decoded.Disposals == 1), "A discarded decode was not disposed exactly once.");
        }
    }

    /// <summary>Dispose racing workers that are still decoding: every decode that finishes is disposed exactly
    /// once, by whichever of Dispose and the worker comes last, and nothing is uploaded.</summary>
    public static async Task DisposeRacingDecodes_DisposesEveryDecodeOnce()
    {
        using var content = Content(3);
        var type = new TextType();
        var workers = new List<Task>();
        var loader = new AssetLoader(content, new AssetLoaderOptions
        {
            Background = work =>
            {
                lock (workers) workers.Add(Task.Run(work));
            },
        });
        loader.Load("/0.txt", type);
        loader.Load("/1.txt", type);
        loader.Load("/2.txt", type);
        await Task.Yield();
        loader.Dispose();

        Task[] all;
        lock (workers) all = [.. workers];
        await Task.WhenAll(all).ConfigureAwait(false);

        Specification.Assert(type.Uploads == 0, "A disposed loader uploaded an asset.");
        lock (type.Decodes)
        {
            Specification.Assert(type.Decodes.All(decoded => decoded.Disposals == 1),
                "A decode finishing around Dispose was stranded or disposed twice.");
        }
    }

    /// <summary>Loading the same asset again while its first decode is in flight shares one decode.</summary>
    public static async Task ReloadRacingDecode_SharesOneDecode()
    {
        using var content = Content(1);
        var type = new TextType();
        using var loader = new AssetLoader(content);
        var first = loader.Load("/0.txt", type);
        await Task.Yield();
        var second = loader.Load("/0.txt", type);

        while (loader.Pending > 0)
        {
            loader.Update();
            await Task.Yield();
        }

        Specification.Assert(ReferenceEquals(first, second), "A second load of one asset made a second handle.");
        Specification.Assert(type.Uploads == 1, $"{type.Uploads} uploads for one shared asset.");
        loader.Release(first);
        loader.Release(second);
        loader.Update();
        Specification.Assert(type.Unloads == 1, $"{type.Unloads} unloads after the last release.");
    }
}
