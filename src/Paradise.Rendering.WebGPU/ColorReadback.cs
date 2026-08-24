namespace Paradise.Rendering.WebGPU;

/// <summary>
/// A rendered frame copied back to CPU memory: tightly-packed, top-down <c>BGRA8</c> (4 bytes per
/// pixel) and the size it was read at.
///
/// A type rather than three out-parameters because it is returned as a whole — see
/// <see cref="WebGpuRenderer.CaptureFrameAsync"/>, which delivers one per requested frame.
/// </summary>
public readonly record struct ColorReadback(byte[] Pixels, uint Width, uint Height);
