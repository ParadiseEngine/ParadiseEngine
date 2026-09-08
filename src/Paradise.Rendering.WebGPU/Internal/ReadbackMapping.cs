using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WebGpuSharp;
using WebGpuSharp.FFI;
using WebGpuSharp.Marshalling;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Maps a readback on the calling thread without Dawn's timed native waits.</summary>
internal static class ReadbackMapping
{
    /// <remarks>The caller owns the buffer exclusively and must unmap it in a finally block,
    /// including on failure. No callback captures managed or stack state: a cancelled mapping
    /// may complete after a timeout. Zero-timeout polling avoids the D3D12 event retention in
    /// the timed queue-wait/map-wait sequence in WebGPUSharp 0.5.7.</remarks>
    internal static unsafe void Map(Instance instance, WebGpuSharp.Buffer buffer, nuint bytes)
    {
        var nativeBuffer = WebGPUMarshal.GetHandle(buffer);
        var nativeInstance = WebGPUMarshal.GetHandle(instance);
        var future = nativeBuffer.MapAsync(MapMode.Read, 0, bytes, new BufferMapCallbackInfoFFI
        {
            Mode = CallbackMode.WaitAnyOnly,
            Callback = &Mapped,
        });
        var info = new FutureWaitInfo { Future = future };
        var started = Stopwatch.GetTimestamp();
        try
        {
            while (!info.Completed)
            {
                var status = nativeInstance.WaitAny(1, &info, 0);
                if (status is not (WaitStatus.Success or WaitStatus.TimedOut))
                    throw new InvalidOperationException($"Readback wait failed: {status}.");
                if (info.Completed) break;
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5))
                    throw new TimeoutException("GPU readback did not complete within five seconds.");
                Thread.Yield();
            }
        }
        catch
        {
            // Abort a pending map and drain its cancellation callback without a timed wait.
            nativeBuffer.Unmap();
            nativeInstance.WaitAny(1, &info, 0);
            throw;
        }
        if (nativeBuffer.GetMapState() != BufferMapState.Mapped)
            throw new InvalidOperationException("GPU readback mapping failed.");
    }

    // Dawn requires a callback even when only the future and final map state are consumed.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void Mapped(MapAsyncStatus status, StringViewFFI message, void* first, void* second)
    {
    }
}
