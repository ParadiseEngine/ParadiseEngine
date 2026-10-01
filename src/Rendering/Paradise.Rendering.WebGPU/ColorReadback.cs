namespace Paradise.Rendering.WebGPU;

/// <summary>Contains a top-down, tightly packed four-channel byte frame and its dimensions.</summary>
/// <remarks>Channel order follows the renderer's color format; window captures may be BGRA or RGBA.</remarks>
public readonly record struct ColorReadback(byte[] Pixels, uint Width, uint Height);
