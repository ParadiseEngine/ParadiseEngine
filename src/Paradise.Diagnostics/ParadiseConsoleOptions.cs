using Microsoft.Extensions.Logging;

namespace Paradise.Diagnostics;

/// <summary>
/// How <see cref="ParadiseConsoleLoggerProvider"/> decides what to print, where to print it, and
/// how to render a value the engine logged.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RenderValue"/> is the reason this type exists. A <c>UPath</c> is <c>/</c>-separated
/// and rooted at its mount, so a physical filesystem renders <c>C:\proj\x</c> as
/// <c>/mnt/c/proj/x</c> — correct inside the abstraction and useless to a person. The layer that
/// produces the message deliberately does not know which filesystem it was handed, so it cannot
/// translate; the host mounted it, so it can. Installing a renderer here is the two-line host
/// concern that makes every path in engine diagnostics read as a host path, with no reader having
/// learned about <c>ConvertPathToInternal</c>.
/// </para>
/// <para>
/// The renderer only runs on a message that reached an enabled level, and only over arguments the
/// caller actually named in its template. A message with no renderer installed takes the fast
/// path and never allocates beyond what the logging call already did.
/// </para>
/// </remarks>
public sealed class ParadiseConsoleOptions
{
    /// <summary>Messages below this level are dropped without being formatted.</summary>
    public LogLevel MinLevel { get; init; } = LogLevel.Information;

    /// <summary>
    /// Renders one logged argument, or returns <see langword="null"/> to fall back to the value's
    /// own <see cref="object.ToString"/>. Called for every argument of every enabled message, so
    /// it should type-test and return quickly.
    /// </summary>
    /// <remarks>
    /// The engine cannot reference Zio's <c>UPath</c> from here without dragging the filesystem
    /// abstraction into every host, so this is <see cref="object"/> and the host type-tests:
    /// <code>
    /// RenderValue = value => value is UPath path &amp;&amp; fileSystem.TryGetPath(path, out var real) ? real : null
    /// </code>
    /// </remarks>
    public Func<object?, string?>? RenderValue { get; init; }

    /// <summary>
    /// Whether to prefix each line with the logger's category in brackets, as the engine's
    /// hand-written <c>[WebGPU]</c> / <c>[PbrRenderer]</c> prefixes used to do.
    /// </summary>
    /// <remarks>
    /// Off for output a person reads as the program talking to them — the CLI's asset-pipeline
    /// progress lines were bare <c>Console.WriteLine</c> before the seam existed and should stay
    /// bare. On for engine diagnostics, where the category is the only thing a reader can filter.
    /// </remarks>
    public bool IncludeCategory { get; init; } = true;

    /// <summary>
    /// The lowest level written to <see cref="Error"/> rather than <see cref="Out"/>. Severity was
    /// previously a convention — <c>Console.Error</c> for bad news — and this keeps it one.
    /// </summary>
    public LogLevel ErrorStreamThreshold { get; init; } = LogLevel.Warning;

    /// <summary>Where non-error lines go. Defaults to <see cref="Console.Out"/> when null.</summary>
    /// <remarks>Settable so a test can assert on what was written; several engine behaviours were
    /// previously observable only as console output, which no test could read.</remarks>
    public TextWriter? Out { get; init; }

    /// <summary>Where error lines go. Defaults to <see cref="Console.Error"/> when null.</summary>
    public TextWriter? Error { get; init; }
}
