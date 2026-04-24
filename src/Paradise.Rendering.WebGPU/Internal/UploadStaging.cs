namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Reserved for a frame-local upload allocator (uniform/storage scratch buffers). The M2
/// upload paths — <see cref="WebGpuSharp.Queue.WriteBuffer"/> and
/// <see cref="WebGpuSharp.Queue.WriteTexture"/> — already stage through Dawn internally and cover
/// the textured-quad sample cleanly, so no Paradise-side allocator is needed for M2. The class
/// exists as a marker for the eventual multi-frame scratch-buffer pool once render graphs or ECS
/// extraction surface per-frame upload volumes that warrant a shared staging buffer.</summary>
internal static class UploadStaging
{
}
