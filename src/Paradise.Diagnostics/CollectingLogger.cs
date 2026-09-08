using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace Paradise.Diagnostics;

/// <summary>One logged message, kept as its level and its rendered text.</summary>
public readonly record struct LogRecord(LogLevel Level, EventId EventId, string Message, Exception? Exception)
{
    /// <inheritdoc />
    public override string ToString() => Message;
}

/// <summary>Collects log records for tests and host diagnostics.</summary>
/// <remarks>Thread-safe, including calls from native callbacks.</remarks>
public sealed class CollectingLogger : ILogger
{
    // Queue order preserves log order; ToArray provides a consistent snapshot without a caller lock.
    private readonly ConcurrentQueue<LogRecord> _records = new();

    /// <summary>Messages at or above this level are kept; the rest are dropped unformatted.</summary>
    public LogLevel MinLevel { get; init; } = LogLevel.Trace;

    /// <summary>Everything logged so far, oldest first.</summary>
    public IReadOnlyList<LogRecord> Records => _records.ToArray();

    /// <summary>Just the rendered text of everything logged so far.</summary>
    public IReadOnlyList<string> Messages =>
        _records.Select(record => record.Message).ToArray();

    /// <summary>The rendered text of everything logged at <paramref name="level"/> or above.</summary>
    public IReadOnlyList<string> MessagesAtLeast(LogLevel level) =>
        _records.Where(record => record.Level >= level).Select(record => record.Message).ToArray();

    /// <summary>Forgets everything kept so far.</summary>
    public void Clear() => _records.Clear();

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= MinLevel;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        ArgumentNullException.ThrowIfNull(formatter);

        _records.Enqueue(new LogRecord(logLevel, eventId, formatter(state, exception), exception));
    }

    /// <inheritdoc />
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        private NullScope() { }
        public void Dispose() { }
    }
}
