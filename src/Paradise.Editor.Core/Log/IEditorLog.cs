namespace Paradise.Editor.Core.Log;

public enum LogSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record LogLine(DateTimeOffset At, LogSeverity Severity, string Message);

/// <summary>The sink the Console panel reads and everything else writes.</summary>
public interface IEditorLog
{
    IReadOnlyList<LogLine> Lines { get; }

    void Write(LogSeverity severity, string message);
}
