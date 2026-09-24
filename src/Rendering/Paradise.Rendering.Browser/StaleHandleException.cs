using System;

namespace Paradise.Rendering.Browser;

/// <summary>Thrown when the browser backend receives a handle whose generation no longer matches
/// its slot — i.e. the resource was destroyed and the slot possibly re-allocated. Signals a
/// use-after-free in the consumer. Deliberately a distinct type from the Dawn backend's
/// same-named exception: the two packages share no assembly, and a browser host never references
/// the desktop one.</summary>
public sealed class StaleHandleException : InvalidOperationException
{
    public StaleHandleException(string message) : base(message) { }
}
