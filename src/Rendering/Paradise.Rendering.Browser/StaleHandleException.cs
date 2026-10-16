using System;

namespace Paradise.Rendering.Browser;

/// <summary>Thrown when a browser resource handle is stale, invalid or was never issued.</summary>
/// <remarks>This backend-local exception does not require a reference to the native WebGPU package.</remarks>
public sealed class StaleHandleException : InvalidOperationException
{
    public StaleHandleException(string message) : base(message) { }
}
