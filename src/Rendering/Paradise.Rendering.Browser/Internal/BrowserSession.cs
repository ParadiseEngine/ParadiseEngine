using System;
using System.Threading;

namespace Paradise.Rendering.Browser.Internal;

/// <summary>The one WebGPU session the JavaScript shim can hold per page.</summary>
/// <remarks>The shim keeps its device, canvas context and resource tables in module state. A
/// second live renderer would overwrite them, and disposing either one would destroy the other's
/// device, so a session is claimed before initialization starts and released when it fails or
/// the renderer is disposed.</remarks>
internal sealed class BrowserSession
{
    private int _claimed;

    /// <summary>The page's session, shared by every renderer the module serves.</summary>
    public static BrowserSession Page { get; } = new();

    /// <summary>Claims the session, or throws while another renderer holds it.</summary>
    /// <exception cref="InvalidOperationException">A renderer is live or still initializing.</exception>
    public void Claim()
    {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "A BrowserRenderer is already live on this page. The WebGPU shim holds one device and " +
                "one set of resource tables, so dispose the existing renderer before creating another.");
        }
    }

    /// <summary>Releases the session after a failed initialization or a disposal.</summary>
    public void Release() => Volatile.Write(ref _claimed, 0);

    /// <summary>Whether a renderer holds the session.</summary>
    public bool IsClaimed => Volatile.Read(ref _claimed) != 0;
}
