using System;

namespace Paradise.Rendering.WebGPU;

/// <summary>Thrown when a native backend resource handle is stale, invalid or was never issued.</summary>
public sealed class StaleHandleException : InvalidOperationException
{
    public StaleHandleException(string message) : base(message) { }
}
