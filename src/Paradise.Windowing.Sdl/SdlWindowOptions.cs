using SDL;

namespace Paradise.Windowing.Sdl;

internal static class SdlWindowOptions
{
    internal static SDL_WindowFlags CreateFlags(in WindowOptions options, bool android)
    {
        var flags = options.Resizable ? SDL_WindowFlags.SDL_WINDOW_RESIZABLE : 0;
        if (options.Fullscreen ?? android)
        {
            // SDL owns Android's immersive system-bar policy as well as the drawable size.
            flags |= SDL_WindowFlags.SDL_WINDOW_FULLSCREEN;
        }
        if (android)
        {
            flags |= SDL_WindowFlags.SDL_WINDOW_VULKAN | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY;
        }
        return flags;
    }
}
