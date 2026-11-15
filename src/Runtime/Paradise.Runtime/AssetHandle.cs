using System.Diagnostics.CodeAnalysis;
using Zio;

namespace Paradise.Runtime;

/// <summary>Where an asset is in its life.</summary>
public enum AssetState : byte
{
    /// <summary>Being read and decoded in the background, or waiting for its upload slot.</summary>
    Loading,

    /// <summary>Uploaded; <see cref="AssetHandle{T}.Value"/> is usable.</summary>
    Ready,

    /// <summary>Reading, decoding or uploading threw; <see cref="AssetHandle{T}.Error"/> says why.</summary>
    Failed,

    /// <summary>Every reference was released; the value is gone or never arrived.</summary>
    Released,
}

/// <summary>A reference to an asset an <see cref="AssetLoader"/> is loading or holds.</summary>
/// <remarks>Read it on the thread that owns the loader (the render thread): the loader changes its state
/// only there, during <see cref="AssetLoader.Update"/>. One handle is shared by every
/// <see cref="AssetLoader.Load{TDecoded, TResident}"/> of the same path and asset type; each load is
/// matched by one <see cref="AssetLoader.Release{T}"/>.</remarks>
public sealed class AssetHandle<T> where T : class
{
    internal AssetHandle(UPath path, object owner)
    {
        Path = path;
        Owner = owner;
    }

    /// <summary>The content path the asset was loaded from.</summary>
    public UPath Path { get; }

    public AssetState State { get; internal set; }

    /// <summary>The uploaded value while <see cref="State"/> is <see cref="AssetState.Ready"/>.</summary>
    public T? Value { get; internal set; }

    /// <summary>What went wrong when <see cref="State"/> is <see cref="AssetState.Failed"/>.</summary>
    public Exception? Error { get; internal set; }

    public bool IsReady => State == AssetState.Ready;

    /// <summary>The loader entry behind this handle.</summary>
    internal object Owner { get; }

    /// <summary>The value, when the asset is ready.</summary>
    public bool TryGet([NotNullWhen(true)] out T? value)
    {
        value = State == AssetState.Ready ? Value : null;
        return value is not null;
    }
}

/// <summary>How one kind of asset is decoded, made resident and released.</summary>
/// <remarks>The loader keys handles by path and by this object, so use one instance per kind of asset.</remarks>
/// <typeparam name="TDecoded">CPU-side result of decoding, produced off the render thread.</typeparam>
/// <typeparam name="TResident">What the game uses, produced on the render thread.</typeparam>
public interface IAssetType<TDecoded, TResident> where TResident : class
{
    /// <summary>Turns the file's bytes into an upload-ready form. Runs on a background worker (or inline
    /// for a single-threaded host), so it must not touch the renderer or other render-thread state.</summary>
    TDecoded Decode(UPath path, byte[] bytes);

    /// <summary>Makes a decoded asset resident: creates GPU resources and the like. Runs on the thread
    /// that calls <see cref="AssetLoader.Update"/>, within its per-frame upload budget.</summary>
    TResident Upload(TDecoded decoded);

    /// <summary>Releases a resident asset. Runs during <see cref="AssetLoader.Update"/> after its last
    /// reference is released, or during <see cref="AssetLoader.Dispose"/>.</summary>
    void Unload(TResident resident);
}
