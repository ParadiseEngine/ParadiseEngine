namespace Paradise.Rendering.WebGPU;

/// <summary>
/// A rendered frame copied back to CPU memory: tightly-packed, top-down <c>BGRA8</c> (4 bytes per
/// pixel) and the size it was read at.
///
/// A type rather than three out-parameters because it is returned as a whole or not at all — see
/// <see cref="WebGpuRenderer.TryReadbackColor"/>, where "there was no image to read" is a normal
/// answer for a renderer that presents to a window.
/// </summary>
public readonly record struct ColorReadback(byte[] Pixels, uint Width, uint Height);
