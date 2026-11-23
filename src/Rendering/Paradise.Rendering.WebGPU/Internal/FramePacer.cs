using System;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Bounds how far frame submission may run ahead of the GPU when the target does not.</summary>
/// <remarks>
/// An offscreen target never blocks, so on a software adapter a headless loop queues hundreds of
/// frames, and the next capture, readback or device destruction must drain them all inside a
/// timeout sized for one frame. Waiting for submitted work just before each submission keeps one
/// frame in flight while the CPU still records the next frame concurrently.
/// </remarks>
internal sealed class FramePacer(bool targetPacesFrames, Action waitForSubmittedWork)
{
    private bool _frameInFlight;

    /// <summary>Call immediately before submitting a frame for presentation.</summary>
    public void BeforeFrameSubmit()
    {
        if (targetPacesFrames) return;
        if (_frameInFlight) waitForSubmittedWork();
        _frameInFlight = true;
    }
}
