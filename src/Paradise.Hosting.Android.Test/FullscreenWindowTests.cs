using Paradise.Windowing;
using Paradise.Windowing.Sdl;
using SDL;
using TUnit.Assertions;
using TUnit.Core;

namespace Paradise.Hosting.Android.Test;

public sealed class FullscreenWindowTests
{
    [Test]
    public async Task unspecified_fullscreen_uses_the_platform_default()
    {
        var options = new WindowOptions("Game", 1280, 720);
        await Assert.That(options.Fullscreen).IsNull();
        await Assert.That(IsFullscreen(options, android: true)).IsTrue();
        await Assert.That(IsFullscreen(options, android: false)).IsFalse();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task explicit_fullscreen_overrides_the_platform_default(bool android)
    {
        var options = new WindowOptions("Game", 1280, 720) { Fullscreen = true };
        await Assert.That(IsFullscreen(options, android)).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task explicit_windowed_overrides_the_platform_default(bool android)
    {
        var options = new WindowOptions("Game", 1280, 720) { Fullscreen = false };
        await Assert.That(IsFullscreen(options, android)).IsFalse();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task mobile_rendering_flags_survive_fullscreen_overrides(bool fullscreen)
    {
        var options = new WindowOptions("Game", 1280, 720) { Fullscreen = fullscreen };
        var flags = SdlWindowOptions.CreateFlags(options, android: true);
        await Assert.That((flags & SDL_WindowFlags.SDL_WINDOW_VULKAN) != 0).IsTrue();
        await Assert.That((flags & SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY) != 0).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task desktop_does_not_receive_mobile_only_flags(bool fullscreen)
    {
        var options = new WindowOptions("Game", 1280, 720) { Fullscreen = fullscreen };
        var flags = SdlWindowOptions.CreateFlags(options, android: false);
        var mobile = SDL_WindowFlags.SDL_WINDOW_VULKAN | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY;
        await Assert.That((flags & mobile) == 0).IsTrue();
    }

    [Test]
    [Arguments(true, true)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(false, false)]
    public async Task resizable_is_independent_of_the_fullscreen_default(bool android, bool resizable)
    {
        var options = new WindowOptions("Game", 1280, 720) { Resizable = resizable };
        var flags = SdlWindowOptions.CreateFlags(options, android);
        await Assert.That((flags & SDL_WindowFlags.SDL_WINDOW_RESIZABLE) != 0).IsEqualTo(resizable);
        await Assert.That((flags & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0).IsEqualTo(android);
    }

    private static bool IsFullscreen(WindowOptions options, bool android) =>
        (SdlWindowOptions.CreateFlags(options, android) & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0;
}
