using System;

namespace Paradise.Rendering.WebGPU;

/// <summary>Thrown when a backend operation receives a handle whose generation no longer matches
/// the slot table — i.e. the underlying resource was destroyed and possibly re-allocated. Signals
/// a use-after-free bug in the consumer.</summary>
public sealed class StaleHandleException : InvalidOperationException
{
    public StaleHandleException(string message) : base(message) { }
}
