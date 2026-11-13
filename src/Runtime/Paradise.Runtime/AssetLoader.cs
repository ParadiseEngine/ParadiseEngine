using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zio;

namespace Paradise.Runtime;

/// <summary>Configures an <see cref="AssetLoader"/>.</summary>
public sealed record AssetLoaderOptions
{
    /// <summary>Most uploads <see cref="AssetLoader.Update"/> performs per call, so a burst of finished
    /// decodes spreads over frames instead of stalling one.</summary>
    public int MaxUploadsPerUpdate { get; init; } = 8;

    /// <summary>Runs read-and-decode work. Null queues it to the thread pool; a single-threaded host
    /// (WebAssembly, a test) passes <c>work => work()</c> to decode inline during
    /// <see cref="AssetLoader.Load{TDecoded, TResident}"/>.</summary>
    public Action<Action>? Background { get; init; }
}

/// <summary>Loads assets by content path into ref-counted handles: read and decode on a background
/// worker, upload on the thread that owns the loader within a per-frame budget, unload when the last
/// reference is released.</summary>
/// <remarks>
/// <para>One thread owns the loader, normally the render thread: it calls
/// <see cref="Load{TDecoded, TResident}"/>, <see cref="Release{T}"/>, <see cref="Update"/> and
/// <see cref="Dispose"/>, and only it sees handles change. Workers touch nothing but the content
/// filesystem, their asset type's <see cref="IAssetType{TDecoded, TResident}.Decode"/> and a
/// concurrent queue that hands results back.</para>
/// <para>Content comes from a Zio <see cref="IFileSystem"/>: a shipped game mounts its content root,
/// a pack or memory. Asset dependencies are the asset type's business; a material type, say, reads its
/// textures in its own decode.</para>
/// </remarks>
public sealed partial class AssetLoader : IDisposable
{
    private readonly IFileSystem _content;
    private readonly ILogger _log;
    private readonly int _maxUploads;
    private readonly Action<Action> _background;
    private readonly Dictionary<(UPath Path, object Type), Entry> _entries = [];
    private readonly ConcurrentQueue<Entry> _decoded = new();
    private readonly Queue<Entry> _uploads = new();
    private readonly List<Entry> _unloads = [];
    private int _inFlight;
    private bool _disposed;

    public AssetLoader(IFileSystem content, AssetLoaderOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        options ??= new AssetLoaderOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxUploadsPerUpdate, 1, nameof(options));
        _content = content;
        _log = logger ?? NullLogger.Instance;
        _maxUploads = options.MaxUploadsPerUpdate;
        _background = options.Background ?? (static work => Task.Run(work));
    }

    /// <summary>Handles still loading: being read or decoded, or waiting to upload.</summary>
    public int Pending { get; private set; }

    /// <summary>Background reads and decodes whose results <see cref="Update"/> has not collected yet,
    /// including those of handles already released.</summary>
    public int InFlight => _inFlight;

    /// <summary>Starts loading <paramref name="path"/> as <paramref name="type"/>, or adds a reference to
    /// the handle already loading or holding it.</summary>
    /// <param name="path">An absolute content path.</param>
    public AssetHandle<TResident> Load<TDecoded, TResident>(UPath path, IAssetType<TDecoded, TResident> type)
        where TResident : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(type);
        if (path.IsNull || !path.IsAbsolute)
            throw new ArgumentException($"Asset path '{path}' must be absolute.", nameof(path));

        var key = (path, (object)type);
        if (_entries.TryGetValue(key, out var existing))
        {
            existing.References++;
            return ((Entry<TDecoded, TResident>)existing).Handle;
        }

        var entry = new Entry<TDecoded, TResident>(this, key, type);
        _entries.Add(key, entry);
        Pending++;
        _inFlight++;
        _background(entry.ReadAndDecode);
        return entry.Handle;
    }

    /// <summary>Drops one reference. The last one unloads a ready asset during the next
    /// <see cref="Update"/>, and discards a loading one when its decode finishes.</summary>
    /// <exception cref="InvalidOperationException">The handle has no references left, or belongs to another loader.</exception>
    public void Release<T>(AssetHandle<T> handle) where T : class
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.Owner is not Entry entry || entry.Loader != this)
            throw new InvalidOperationException($"Asset '{handle.Path}' was not loaded by this loader.");
        if (entry.References == 0)
            throw new InvalidOperationException($"Asset '{handle.Path}' was released more times than it was loaded.");
        if (--entry.References > 0) return;

        _entries.Remove(entry.Key);
        entry.ReleaseRequested = true;
        switch (entry.State)
        {
            case AssetState.Ready:
                _unloads.Add(entry);
                break;
            case AssetState.Failed:
                entry.MarkReleased();
                break;
            case AssetState.Loading:
                // Still decoding or queued for upload: the result is discarded when it arrives.
                Pending--;
                entry.MarkReleased();
                break;
        }
    }

    /// <summary>Unloads released assets, collects finished decodes and uploads up to the budget. Call once
    /// per frame on the owning thread.</summary>
    /// <returns>How many assets became ready.</returns>
    public int Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var entry in _unloads) Unload(entry);
        _unloads.Clear();

        while (_decoded.TryDequeue(out var entry))
        {
            _inFlight--;
            if (entry.ReleaseRequested)
            {
                entry.DiscardDecoded();
            }
            else if (entry.DecodeFailure is { } failure)
            {
                Fail(entry, failure);
            }
            else
            {
                _uploads.Enqueue(entry);
            }
        }

        var uploaded = 0;
        while (uploaded < _maxUploads && _uploads.TryDequeue(out var entry))
        {
            if (entry.ReleaseRequested)
            {
                entry.DiscardDecoded();
                continue;
            }

            try
            {
                entry.Upload();
                Pending--;
                uploaded++;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Fail(entry, error);
            }
        }

        return uploaded;
    }

    /// <summary>Unloads every resident asset. Decodes still running are not waited for; their results
    /// are dropped.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _unloads) Unload(entry);
        _unloads.Clear();
        foreach (var entry in _entries.Values)
        {
            entry.ReleaseRequested = true;
            if (entry.State == AssetState.Ready) Unload(entry);
            else entry.MarkReleased();
        }

        _entries.Clear();
        while (_uploads.TryDequeue(out var queued)) queued.DiscardDecoded();
        while (_decoded.TryDequeue(out var finished)) finished.DiscardDecoded();
        Pending = 0;
    }

    private void Fail(Entry entry, Exception error)
    {
        Pending--;
        entry.MarkFailed(error);
        LogLoadFailed(_log, error, entry.Key.Path.FullName);
    }

    private void Unload(Entry entry)
    {
        try
        {
            entry.Unload();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            LogUnloadFailed(_log, error, entry.Key.Path.FullName);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Asset '{Path}' failed to load.")]
    private static partial void LogLoadFailed(ILogger logger, Exception error, string path);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Asset '{Path}' failed to unload.")]
    private static partial void LogUnloadFailed(ILogger logger, Exception error, string path);

    /// <summary>One path and asset type. Fields are owned by the loader's thread, except the decode
    /// result, which a worker writes before handing the entry over through the concurrent queue.</summary>
    private abstract class Entry(AssetLoader loader, (UPath Path, object Type) key)
    {
        public AssetLoader Loader { get; } = loader;
        public (UPath Path, object Type) Key { get; } = key;
        public int References = 1;
        public bool ReleaseRequested;
        public Exception? DecodeFailure;

        public abstract AssetState State { get; }
        public abstract void Upload();
        public abstract void Unload();
        public abstract void MarkFailed(Exception error);
        public abstract void MarkReleased();
        public abstract void DiscardDecoded();
    }

    private sealed class Entry<TDecoded, TResident>(AssetLoader loader, (UPath Path, object Type) key, IAssetType<TDecoded, TResident> type)
        : Entry(loader, key) where TResident : class
    {
        private TDecoded? _decoded;
        private bool _hasDecoded;

        // The handle names the entry as its owner, so it is made on first use, which is Load on the
        // loader's thread; workers never touch it.
        private AssetHandle<TResident>? _handle;

        public AssetHandle<TResident> Handle => HandleOf;

        public override AssetState State => HandleOf.State;

        private AssetHandle<TResident> HandleOf => _handle ??= new AssetHandle<TResident>(Key.Path, this);

        public void ReadAndDecode()
        {
            try
            {
                var bytes = Loader._content.ReadAllBytes(Key.Path);
                _decoded = type.Decode(Key.Path, bytes);
                _hasDecoded = true;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                DecodeFailure = error;
            }
            finally
            {
                // Publishes the fields above to the loader's thread.
                Loader._decoded.Enqueue(this);
            }
        }

        public override void Upload()
        {
            var resident = type.Upload(_decoded!);
            DropDecoded(dispose: false);
            HandleOf.Value = resident;
            HandleOf.State = AssetState.Ready;
        }

        public override void Unload()
        {
            var resident = HandleOf.Value;
            HandleOf.Value = null;
            HandleOf.State = AssetState.Released;
            if (resident is not null) type.Unload(resident);
        }

        public override void MarkFailed(Exception error)
        {
            DropDecoded(dispose: true);
            HandleOf.Error = error;
            HandleOf.State = AssetState.Failed;
        }

        public override void MarkReleased()
        {
            if (HandleOf.State != AssetState.Ready) HandleOf.State = AssetState.Released;
        }

        public override void DiscardDecoded() => DropDecoded(dispose: true);

        private void DropDecoded(bool dispose)
        {
            if (dispose && _hasDecoded && _decoded is IDisposable disposable) disposable.Dispose();
            _decoded = default;
            _hasDecoded = false;
        }
    }
}
