using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace Paradise.Diagnostics;

/// <summary>One logged message, kept as its level and its rendered text.</summary>
public readonly record struct LogRecord(LogLevel Level, EventId EventId, string Message, Exception? Exception)
{
    /// <inheritdoc />
    public override string ToString() => Message;
}

/// <summary>An <see cref="ILogger"/> that keeps what it was told, for a test to assert on.</summary>
/// <remarks>
/// <para>
/// Several engine behaviours used to be observable only as console output, which no test could
/// read — a sidecar being left alone, a stale output that could not be swept, a cache turning
/// itself off. Passing one of these instead of a real sink is what makes those assertable, and it
/// is why issue #232 counted "something a test can assert against" as part of the seam rather than
/// a nicety.
/// </para>
/// <para>
/// Ships in the product package rather than a test helper because the engine's consumers need it
/// for the same reason its own tests do: a game asserting that its importer chain reported a
/// problem has no other way in. It is also safe to call from any thread, since the engine logs
/// from threads it did not create.
/// </para>
/// </remarks>
public sealed class CollectingLogger : ILogger
{
    // ConcurrentQueue rather than a lock around a List: every operation here is single-step —
    // append one record, snapshot, clear — so there is nothing for a lock to make atomic that
    // the queue does not already. It matters because the engine logs from threads it did not
    // create (Dawn's uncaptured-error callback, Noesis, SDL), and a native callback blocking on
    // a managed lock is the one cost worth avoiding on this path.
    //
    // Enqueue order IS the log order, and ToArray takes a consistent snapshot, so `Records`
    // stays oldest-first without holding anything. Coyote schedules concurrent collections as
    // well as locks (AGENTS.md), so the ArtifactCache suite still explores interleavings here.
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
