using Microsoft.Extensions.Logging;
using Paradise.Features;
using Paradise.Windowing;
using Paradise.Windowing.Sdl;

namespace Paradise.Hosting;

/// <summary>Runs an application over SDL or an offscreen window.</summary>
public static class DesktopHost
{
    public static int Run(IHostApplication application, HostOptions options, FeatureSwitches switches, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(switches);
        ArgumentNullException.ThrowIfNull(logger);
        IWindowPlatform platform = options.Headless
            ? new HeadlessWindowPlatform(options.Hold)
            : new SdlWindowPlatform(logger);
        return ParadiseHost.Run(application, platform, options, switches, logger);
    }
}
