using System.Buffers;
using System.Numerics;

namespace Paradise.Rendering.ECS;

/// <summary>
/// A single extracted renderable: fully resolved, renderer-facing data for one draw call.
/// Frame-local; rebuilt every frame from ECS state by <see cref="Systems.ExtractRenderablesSystem"/>.
/// </summary>
public readonly struct ExtractedRenderable
{
    public Matrix4x4 LocalToWorld { get; init; }
    public uint MeshId { get; init; }
    public uint MaterialId { get; init; }
}

/// <summary>
/// A single extracted camera: view-projection matrix and optional render-view override.
/// Frame-local; rebuilt every frame from ECS state by <see cref="Systems.ExtractCamerasSystem"/>.
/// </summary>
public readonly struct ExtractedCamera
{
    /// <summary>View * Projection, pre-multiplied at extract time.</summary>
    public Matrix4x4 ViewProjection { get; init; }

    /// <summary>
    /// Resolved render-view handle. <see cref="RenderViewHandle.Invalid"/> targets the swapchain.
    /// </summary>
    public RenderViewHandle TargetView { get; init; }
}

/// <summary>
/// Frame-local container for all extracted rendering data.
/// Uses <see cref="ArrayPool{T}"/> buffers so steady-state operation produces zero GC pressure.
/// Call <see cref="Reset"/> at the start of each frame before running the extraction stage.
/// </summary>
public sealed class FrameRenderPackets
{
    private ExtractedRenderable[] _renderablesBuffer;
    private ExtractedCamera[] _camerasBuffer;
    private int _renderableCount;
    private int _cameraCount;

    public FrameRenderPackets()
    {
        _renderablesBuffer = ArrayPool<ExtractedRenderable>.Shared.Rent(64);
        _camerasBuffer = ArrayPool<ExtractedCamera>.Shared.Rent(8);
    }

    /// <summary>All renderables accumulated this frame.</summary>
    public ReadOnlyMemory<ExtractedRenderable> Renderables => _renderablesBuffer.AsMemory(0, _renderableCount);

    /// <summary>All cameras accumulated this frame.</summary>
    public ReadOnlyMemory<ExtractedCamera> Cameras => _camerasBuffer.AsMemory(0, _cameraCount);

    /// <summary>
    /// Clears packet counts so this frame's extraction can repopulate the buffers.
    /// Does not release or reallocate backing arrays — zero GC pressure.
    /// </summary>
    public void Reset()
    {
        _renderableCount = 0;
        _cameraCount = 0;
    }

    /// <summary>Appends one renderable. Called by <see cref="Systems.ExtractRenderablesSystem"/>.</summary>
    internal void AppendRenderable(in ExtractedRenderable r)
    {
        if (_renderableCount == _renderablesBuffer.Length)
            GrowRenderables();
        _renderablesBuffer[_renderableCount++] = r;
    }

    /// <summary>Appends one camera. Called by <see cref="Systems.ExtractCamerasSystem"/>.</summary>
    internal void AppendCamera(in ExtractedCamera c)
    {
        if (_cameraCount == _camerasBuffer.Length)
            GrowCameras();
        _camerasBuffer[_cameraCount++] = c;
    }

    private void GrowRenderables()
    {
        var next = ArrayPool<ExtractedRenderable>.Shared.Rent(_renderablesBuffer.Length * 2);
        _renderablesBuffer.AsSpan(0, _renderableCount).CopyTo(next);
        ArrayPool<ExtractedRenderable>.Shared.Return(_renderablesBuffer);
        _renderablesBuffer = next;
    }

    private void GrowCameras()
    {
        var next = ArrayPool<ExtractedCamera>.Shared.Rent(_camerasBuffer.Length * 2);
        _camerasBuffer.AsSpan(0, _cameraCount).CopyTo(next);
        ArrayPool<ExtractedCamera>.Shared.Return(_camerasBuffer);
        _camerasBuffer = next;
    }
}

/// <summary>
/// Thread-local context that extraction systems use to locate the current frame's packet buffer.
/// Set <see cref="Packets"/> before running the extraction schedule; leave it as <c>null</c> when idle.
/// </summary>
public static class ExtractionContext
{
    [ThreadStatic]
    private static FrameRenderPackets? s_packets;

    /// <summary>
    /// The packet buffer being populated by the current extraction pass.
    /// <c>null</c> outside an active extraction pass.
    /// </summary>
    public static FrameRenderPackets? Packets
    {
        get => s_packets;
        set => s_packets = value;
    }
}
