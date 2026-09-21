namespace Paradise.Rendering.WebGPU;

/// <summary>Contains a top-down, tightly packed BGRA8 frame and its dimensions.</summary>
public readonly record struct ColorReadback(byte[] Pixels, uint Width, uint Height);
