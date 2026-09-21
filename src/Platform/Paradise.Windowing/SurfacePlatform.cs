namespace Paradise.Windowing;

/// <summary>Native windowing platform of a <see cref="SurfaceDescriptor"/>. Selects which
/// native handle the rendering backend consumes.</summary>
public enum SurfacePlatform : byte
{
    Unknown = 0,
    Win32,
    Xlib,
    Wayland,
    Cocoa,
    Headless,
}
