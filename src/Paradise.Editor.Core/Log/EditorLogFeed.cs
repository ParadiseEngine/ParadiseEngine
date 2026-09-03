using Microsoft.Extensions.Logging;

namespace Paradise.Editor.Core.Log;

/// <summary>One line the Console panel draws.</summary>
public readonly record struct EditorLogEntry(
    DateTimeOffset At, LogLevel Level, string Category, string Message, Exception? Exception);

/// <summary>
/// The READ half of the editor's console. The write half is plain <see cref="ILogger"/>.
/// </summary>
/// <remarks>
/// <para>
/// The editor reports through the engine's seam (AGENTS.md, <c>docs/logging.md</c>): it takes an
/// <see cref="ILogger"/>, references Abstractions only, and never names a sink. That is what makes
/// the in-game host work at all — embedded in a game, editor diagnostics land in the game's own
/// logging stack instead of a second one nobody is watching.
/// </para>
/// <para>
/// But a console PANEL has to read back, and <see cref="ILogger"/> deliberately cannot: it is a
/// write-only contract with no history and no enumeration. So the host installs a provider that
/// both forwards and keeps, and hands the keeping half in as this. Standalone that is
/// <c>Paradise.Diagnostics</c>'s <c>CollectingLogger</c> behind an adapter; in-game it is whatever
/// the game already logs through, or nothing at all — a host that supplies no feed simply has no
/// Console panel, which is the right outcome rather than a missing-sink crash.
/// </para>
/// <para>
/// Read every frame by an immediate-mode panel, so <see cref="Entries"/> must be cheap and must
/// not allocate a fresh snapshot per access. There is no change event because nothing here polls
/// less often than the frame does.
/// </para>
/// </remarks>
public interface IEditorLogFeed
{
    IReadOnlyList<EditorLogEntry> Entries { get; }

    void Clear();
}
