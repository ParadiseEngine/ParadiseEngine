using SDL;
using static SDL.SDL3;

namespace Paradise.Windowing.Sdl;

/// <summary>
/// The SDL3 backend's platform half: initializes SDL's video subsystem for its lifetime and
/// creates <see cref="SdlWindow"/>s. One instance per process; dispose windows before it.
/// Main thread throughout — see the contract on <see cref="IWindowPlatform"/>.
/// </summary>
public sealed class SdlWindowPlatform : IWindowPlatform
{
    private bool _disposed;

    public SdlWindowPlatform()
    {
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            throw new InvalidOperationException($"SDL_Init failed: {SDL_GetError()}");
        }
    }

    public IWindow CreateWindow(in WindowOptions options) => new SdlWindow(in options);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        SDL_Quit();
    }
}
