namespace Paradise.Rendering;

/// <summary>RGBA color with float components in [0,1]. Used for clear values and uniform inputs.
/// Color space is consumer-defined: backends interpret the components according to the bound
/// render attachment's format (e.g. an <c>Rgba8UnormSrgb</c> attachment treats the value as sRGB,
/// an <c>Rgba8Unorm</c> attachment treats it as linear). The named constants below use the
/// canonical sRGB shorthand familiar from XNA / WPF — convert to linear at the call site if your
/// pipeline requires it.</summary>
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
