using System.Diagnostics;
using System.Text;
using Zio;
using Zio.FileSystems;

namespace Paradise.Runtime.Test;

public class AssetLoaderTests
{
    /// <summary>Text files as assets: decoding yields a disposable payload, so discards are observable.</summary>
    private sealed class TextType : IAssetType<TextType.Decoded, TextType.Resident>
    {
        public sealed class Decoded(string text) : IDisposable
        {
            public string Text { get; } = text;
            public bool Disposed { get; private set; }
            public void Dispose() => Disposed = true;
        }

        public sealed class Resident(string text)
        {
            public string Text { get; } = text;
        }

        public List<Decoded> Decodes { get; } = [];
        public int Uploads;
        public List<string> Unloaded { get; } = [];
        public string? FailUploadOf;

        public Decoded Decode(UPath path, byte[] bytes)
        {
            var decoded = new Decoded(Encoding.UTF8.GetString(bytes));
            lock (Decodes) Decodes.Add(decoded);
            return decoded;
        }

        public Resident Upload(Decoded decoded)
        {
            if (decoded.Text == FailUploadOf) throw new InvalidOperationException("upload failed");
            Uploads++;
            return new Resident(decoded.Text);
        }

        public void Unload(Resident resident) => Unloaded.Add(resident.Text);
    }

    private static readonly AssetLoaderOptions s_inline = new() { Background = static work => work() };

    private static MemoryFileSystem Content(int files)
    {
        var fs = new MemoryFileSystem();
        fs.CreateDirectory("/text");
        for (var i = 0; i < files; i++) fs.WriteAllText($"/text/{i}.txt", $"asset {i}");
        return fs;
    }

    [Test]
    public async Task finished_decodes_upload_within_the_per_update_budget()
    {
        using var content = Content(5);
        var type = new TextType();
        using var loader = new AssetLoader(content, s_inline with { MaxUploadsPerUpdate = 2 });
        var handles = Enumerable.Range(0, 5).Select(i => loader.Load($"/text/{i}.txt", type)).ToArray();

        await Assert.That(loader.Pending).IsEqualTo(5);
        await Assert.That(handles.All(handle => handle.State == AssetState.Loading)).IsTrue();
        await Assert.That(loader.Update()).IsEqualTo(2);
        await Assert.That(loader.Update()).IsEqualTo(2);
        await Assert.That(loader.Update()).IsEqualTo(1);
        await Assert.That(loader.Pending).IsEqualTo(0);
        for (var i = 0; i < 5; i++)
        {
            await Assert.That(handles[i].TryGet(out var value)).IsTrue();
            await Assert.That(value!.Text).IsEqualTo($"asset {i}");
        }
    }

    [Test]
    public async Task one_path_and_type_share_a_handle_until_its_last_release()
    {
        using var content = Content(1);
        var type = new TextType();
        using var loader = new AssetLoader(content, s_inline);
        var first = loader.Load("/text/0.txt", type);
        var second = loader.Load("/text/0.txt", type);
        loader.Update();

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await Assert.That(type.Decodes.Count).IsEqualTo(1);
        loader.Release(first);
        loader.Update();
        await Assert.That(first.IsReady).IsTrue();
        loader.Release(second);
        await Assert.That(first.IsReady).IsTrue(); // unloads at the next update, on the owning thread
        loader.Update();
        await Assert.That(first.State).IsEqualTo(AssetState.Released);
        await Assert.That(type.Unloaded).IsEquivalentTo(new[] { "asset 0" });
        await Assert.That(() => loader.Release(first)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task a_missing_file_fails_its_handle_and_nothing_else()
    {
        using var content = Content(1);
        var type = new TextType();
        using var loader = new AssetLoader(content, s_inline);
        var missing = loader.Load("/text/missing.txt", type);
        var present = loader.Load("/text/0.txt", type);
        loader.Update();

        await Assert.That(missing.State).IsEqualTo(AssetState.Failed);
        await Assert.That(missing.Error).IsTypeOf<FileNotFoundException>();
        await Assert.That(present.IsReady).IsTrue();
        await Assert.That(loader.Pending).IsEqualTo(0);
    }

    [Test]
    public async Task a_failing_upload_fails_that_handle_and_releases_its_decode()
    {
        using var content = Content(2);
        var type = new TextType { FailUploadOf = "asset 1" };
        using var loader = new AssetLoader(content, s_inline);
        var good = loader.Load("/text/0.txt", type);
        var bad = loader.Load("/text/1.txt", type);
        loader.Update();

        await Assert.That(good.IsReady).IsTrue();
        await Assert.That(bad.State).IsEqualTo(AssetState.Failed);
        await Assert.That(bad.Error!.Message).IsEqualTo("upload failed");
        await Assert.That(type.Decodes.Single(decoded => decoded.Text == "asset 1").Disposed).IsTrue();
    }

    [Test]
    public async Task releasing_while_loading_discards_the_decode_without_uploading()
    {
        using var content = Content(1);
        var type = new TextType();
        var queued = new List<Action>();
        using var loader = new AssetLoader(content, new AssetLoaderOptions { Background = queued.Add });
        var handle = loader.Load("/text/0.txt", type);
        loader.Release(handle);

        await Assert.That(handle.State).IsEqualTo(AssetState.Released);
        await Assert.That(loader.Pending).IsEqualTo(0);
        await Assert.That(loader.InFlight).IsEqualTo(1);
        foreach (var work in queued) work();
        loader.Update();

        await Assert.That(loader.InFlight).IsEqualTo(0);
        await Assert.That(type.Uploads).IsEqualTo(0);
        await Assert.That(type.Decodes.Single().Disposed).IsTrue();
        await Assert.That(handle.State).IsEqualTo(AssetState.Released);
    }

    [Test]
    public async Task disposing_unloads_every_resident_asset()
    {
        using var content = Content(3);
        var type = new TextType();
        var loader = new AssetLoader(content, s_inline);
        var handles = Enumerable.Range(0, 3).Select(i => loader.Load($"/text/{i}.txt", type)).ToArray();
        loader.Update();
        loader.Dispose();

        await Assert.That(type.Unloaded.Count).IsEqualTo(3);
        await Assert.That(handles.All(handle => handle.State == AssetState.Released)).IsTrue();
        await Assert.That(() => loader.Load("/text/0.txt", type)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task thread_pool_decodes_reach_the_owning_thread()
    {
        using var content = Content(32);
        var type = new TextType();
        using var loader = new AssetLoader(content);
        var handles = Enumerable.Range(0, 32).Select(i => loader.Load($"/text/{i}.txt", type)).ToArray();
        var clock = Stopwatch.StartNew();
        while (loader.Pending > 0 && clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            loader.Update();
            await Task.Delay(1).ConfigureAwait(false);
        }

        await Assert.That(loader.Pending).IsEqualTo(0);
        await Assert.That(handles.All(handle => handle.IsReady)).IsTrue();
        await Assert.That(type.Uploads).IsEqualTo(32);
    }

    [Test]
    public async Task paths_must_be_absolute()
    {
        using var content = Content(0);
        using var loader = new AssetLoader(content, s_inline);
        await Assert.That(() => loader.Load("text/0.txt", new TextType())).Throws<ArgumentException>();
    }
}
