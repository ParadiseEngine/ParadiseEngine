using System;

namespace Paradise.Rendering.WebGPU;

/// <summary>Thrown when the WebGPU instance cannot acquire any adapter — typically because the
/// host has no Vulkan/Metal/DX12 backend available (CI without lavapipe, headless container
/// without GPU drivers). Distinct from generic <see cref="InvalidOperationException"/> so callers
/// (notably CI smoke tests) can skip cleanly on adapter unavailability without also skipping real
/// device-create regressions.</summary>
public sealed class AdapterUnavailableException : Exception
{
    public AdapterUnavailableException(string message) : base(message) { }
    public AdapterUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
