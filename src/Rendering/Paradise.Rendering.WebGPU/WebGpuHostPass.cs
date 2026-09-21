using System;

namespace Paradise.Rendering.WebGPU;

/// <summary>Records native WebGPU commands in a frame-graph host pass.</summary>
/// <remarks>The callback runs on the render thread and must load and store its target and close
/// all native passes before returning. Keep captured resources alive until submission completes.</remarks>
public sealed class WebGpuHostPass(Action<WebGpuSharp.CommandEncoder, WebGpuSharp.TextureView> record) : HostRenderPass
{
    private readonly Action<WebGpuSharp.CommandEncoder, WebGpuSharp.TextureView> _record = record
        ?? throw new ArgumentNullException(nameof(record));

    internal void Record(WebGpuSharp.CommandEncoder encoder, WebGpuSharp.TextureView target) => _record(encoder, target);
}
