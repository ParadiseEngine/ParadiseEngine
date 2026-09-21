using Microsoft.Extensions.Logging;

namespace Paradise.Hosting.Android;

/// <summary>Runs application code inside SDL's native entry-point exception boundary.</summary>
/// <remarks>SDL's Java Activity initializes JNI and invokes the application's exported SDL_main
/// on its own thread. This host does not own the Activity or implement surface recovery.</remarks>
public static partial class SdlAndroidHost
{
    public static int Run(Action<ILogger> application) => Run(application, new AndroidLogger("Paradise"));

    public static int Run(Action<ILogger> application, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            application(logger);
            return 0;
        }
        catch (Exception exception)
        {
            // A failing diagnostic sink must not let an exception escape into SDL/JNI.
            try
            {
                LogApplicationFailed(logger, exception);
            }
            catch (Exception)
            {
            }
            return 1;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Critical, Message = "Android NativeAOT application failed; inspect logcat.")]
    private static partial void LogApplicationFailed(ILogger logger, Exception exception);
}
