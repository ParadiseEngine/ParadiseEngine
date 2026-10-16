namespace Paradise.Rendering;

/// <summary>RGBA color with unclamped float components for clear values and uniform inputs.</summary>
/// <remarks>No color-space conversion is performed by this type. CornflowerBlue uses sRGB-encoded
/// components; convert it to linear when the consuming shader or attachment operation expects
/// linear values, including clears of sRGB render attachments.</remarks>
public readonly record struct ColorRgba(float R, float G, float B, float A)
{
    public static readonly ColorRgba Black = new(0f, 0f, 0f, 1f);
    public static readonly ColorRgba White = new(1f, 1f, 1f, 1f);
    public static readonly ColorRgba Red = new(1f, 0f, 0f, 1f);
    public static readonly ColorRgba Green = new(0f, 1f, 0f, 1f);
    public static readonly ColorRgba Blue = new(0f, 0f, 1f, 1f);
    public static readonly ColorRgba Transparent = new(0f, 0f, 0f, 0f);
    public static readonly ColorRgba CornflowerBlue = new(0.392f, 0.584f, 0.929f, 1f);
}
