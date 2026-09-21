using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Paradise.Hosting.Android;

/// <summary>Writes diagnostics directly to Android logcat without Java/.NET runtime bindings.</summary>
public sealed partial class AndroidLogger : ILogger
{
    private readonly string _tag;

    public AndroidLogger(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        _tag = tag;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        ArgumentNullException.ThrowIfNull(formatter);
        var message = formatter(state, exception);
        if (exception is not null) message = string.Concat(message, Environment.NewLine, exception.ToString());
        var priority = logLevel switch
        {
            LogLevel.Trace => 2,
            LogLevel.Debug => 3,
            LogLevel.Information => 4,
            LogLevel.Warning => 5,
            LogLevel.Error => 6,
            LogLevel.Critical => 7,
            _ => 4,
        };
        WriteLog(priority, _tag, message);
    }

    [LibraryImport("liblog.so", EntryPoint = "__android_log_write", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int WriteLog(int priority, string tag, string text);
}
