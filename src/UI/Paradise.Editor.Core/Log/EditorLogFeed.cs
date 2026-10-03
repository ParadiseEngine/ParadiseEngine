using Microsoft.Extensions.Logging;

namespace Paradise.Editor.Core.Log;

/// <summary>One line the Console panel draws.</summary>
public readonly record struct EditorLogEntry(
    DateTimeOffset At, LogLevel Level, string Category, string Message, Exception? Exception);

/// <summary>The READ half of the editor's console. The write half is plain <see cref="ILogger"/>.</summary>
/// <remarks>
/// Hosts supply this readback contract alongside their ILogger sink; the core does not choose a
/// provider or adapt one automatically. Cache Entries for per-frame readers rather than allocating
/// a new snapshot on every access. Hosts may omit the console panel when no feed is available.
/// </remarks>
public interface IEditorLogFeed
{
    IReadOnlyList<EditorLogEntry> Entries { get; }

    void Clear();
}
