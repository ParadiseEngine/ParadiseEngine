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

    /// <summary>Holds its decode or upload until released, so a test can act while a call is inside it.</summary>
    private sealed class GatedType : IAssetType<TextType.Decoded, TextType.Resident>
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Proceed { get; } = new();
        public bool GateDecode;
        public bool GateUpload;
        public TextType.Decoded? LastDecoded;
        public Action? DuringUpload;

        public TextType.Decoded Decode(UPath path, byte[] bytes)
        {
            var decoded = new TextType.Decoded(Encoding.UTF8.GetString(bytes));
            LastDecoded = decoded;
            if (GateDecode) Hold();
            return decoded;
        }

        public TextType.Resident Upload(TextType.Decoded decoded)
        {
            DuringUpload?.Invoke();
            if (GateUpload) Hold();
            return new TextType.Resident(decoded.Text);
        }

        public void Unload(TextType.Resident resident) { }

        private void Hold()
        {
            Entered.Set();
            if (!Proceed.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("The test never released the gate.");
        }
    }

    private static Task Dedicated(Action work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    [Test]
    public async Task a_second_thread_inside_the_loader_is_refused()
    {
        using var content = Content(2);
        var gated = new GatedType { GateUpload = true };
        using var loader = new AssetLoader(content, s_inline);
        loader.Load("/text/0.txt", gated);

        var update = Dedicated(() => loader.Update());
        await Assert.That(gated.Entered.Wait(TimeSpan.FromSeconds(30))).IsTrue();
        Exception? refused = null;
        await Dedicated(() =>
        {
            try { loader.Load("/text/1.txt", new TextType()); }
            catch (InvalidOperationException error) { refused = error; }
        }).ConfigureAwait(false);
        gated.Proceed.Set();
        await update.ConfigureAwait(false);

        await Assert.That(refused).IsNotNull();
        // Once the first call has left, any thread may be the owner again.
        await Dedicated(() => loader.Update()).ConfigureAwait(false);
    }

    [Test]
    public async Task a_refused_dispose_leaves_the_loader_to_a_later_dispose()
    {
        using var content = Content(2);
        var gated = new GatedType { GateUpload = true };
        var plain = new TextType();
        var loader = new AssetLoader(content, s_inline);
        var resident = loader.Load("/text/1.txt", plain);
        loader.Update();
        loader.Load("/text/0.txt", gated);

        var update = Dedicated(() => loader.Update());
        await Assert.That(gated.Entered.Wait(TimeSpan.FromSeconds(30))).IsTrue();
        Exception? refused = null;
        await Dedicated(() =>
        {
            try { loader.Dispose(); }
            catch (InvalidOperationException error) { refused = error; }
        }).ConfigureAwait(false);
        gated.Proceed.Set();
        await update.ConfigureAwait(false);

        await Assert.That(refused).IsNotNull();
        await Assert.That(resident.IsReady).IsTrue();
        loader.Dispose();
        await Assert.That(resident.State).IsEqualTo(AssetState.Released);
        await Assert.That(plain.Unloaded).IsEquivalentTo(new[] { "asset 1" });
    }

    [Test]
    public async Task an_asset_type_may_load_from_inside_its_own_upload()
    {
        using var content = Content(2);
        var dependency = new TextType();
        var gated = new GatedType();
        using var loader = new AssetLoader(content, s_inline);
        AssetHandle<TextType.Resident>? loaded = null;
        gated.DuringUpload = () => loaded ??= loader.Load("/text/1.txt", dependency);

        var handle = loader.Load("/text/0.txt", gated);
        loader.Update();
        loader.Update();

        await Assert.That(handle.IsReady).IsTrue();
        await Assert.That(loaded!.IsReady).IsTrue();
    }

    [Test]
    public async Task a_decode_finishing_after_dispose_is_disposed()
    {
        using var content = Content(1);
        var gated = new GatedType { GateDecode = true };
        Task? worker = null;
        var loader = new AssetLoader(content, new AssetLoaderOptions { Background = work => worker = Dedicated(work) });
        loader.Load("/text/0.txt", gated);
        await Assert.That(gated.Entered.Wait(TimeSpan.FromSeconds(30))).IsTrue();

        loader.Dispose();
        gated.Proceed.Set();
        await worker!.ConfigureAwait(false);

        await Assert.That(gated.LastDecoded!.Disposed).IsTrue();
    }

    [Test]
    public async Task paths_must_be_absolute()
    {
        using var content = Content(0);
        using var loader = new AssetLoader(content, s_inline);
        await Assert.That(() => loader.Load("text/0.txt", new TextType())).Throws<ArgumentException>();
    }
}
