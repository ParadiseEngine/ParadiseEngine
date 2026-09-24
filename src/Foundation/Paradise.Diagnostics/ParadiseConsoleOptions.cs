using Microsoft.Extensions.Logging;

namespace Paradise.Diagnostics;

/// <summary>Controls console log levels, stream routing and argument formatting.</summary>
/// <remarks>
/// Hosts can render mounted paths through RenderValue because they own the filesystem mapping.
/// Rendering runs only for arguments of enabled messages.
/// </remarks>
public sealed class ParadiseConsoleOptions
{
    /// <summary>Messages below this level are dropped without being formatted.</summary>
    public LogLevel MinLevel { get; init; } = LogLevel.Information;

    /// <summary>Renders an argument, or returns null to use its default formatting.</summary>
    /// <remarks>
    /// Keep this callback fast; its object parameter avoids a dependency on host value types.
    /// <code>
    /// RenderValue = value => value is UPath path &amp;&amp; fileSystem.TryGetPath(path, out var real) ? real : null
    /// </code>
    /// </remarks>
    public Func<object?, string?>? RenderValue { get; init; }

    /// <summary>Prefixes lines with the logger category in brackets.</summary>
    public bool IncludeCategory { get; init; } = true;

    /// <summary>The lowest level routed to Error instead of Out.</summary>
    public LogLevel ErrorStreamThreshold { get; init; } = LogLevel.Warning;

    /// <summary>Where non-error lines go. Defaults to <see cref="Console.Out"/> when null.</summary>
    public TextWriter? Out { get; init; }

    /// <summary>Where error lines go. Defaults to <see cref="Console.Error"/> when null.</summary>
    public TextWriter? Error { get; init; }
}
