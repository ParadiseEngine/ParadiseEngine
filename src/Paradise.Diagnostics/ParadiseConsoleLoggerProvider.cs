using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

namespace Paradise.Diagnostics;

/// <summary>
/// An <see cref="ILoggerProvider"/> that writes engine diagnostics to a pair of text writers,
/// routing by level and rendering logged values through <see cref="ParadiseConsoleOptions.RenderValue"/>.
/// </summary>
/// <remarks>
/// Every logger this hands out shares one lock, because the engine logs from threads it did not
/// create — Dawn's uncaptured-error callback, Noesis's log callback and SDL all arrive on foreign
/// threads, and two of them interleaving mid-line is how a device-lost report becomes unreadable.
/// <see cref="ILogger"/> itself promises no thread affinity, so this is a property every sink
/// behind this seam has to supply; a host installing its own is responsible for the same.
/// </remarks>
public sealed class ParadiseConsoleLoggerProvider : ILoggerProvider
{
    private readonly ParadiseConsoleOptions _options;
    private readonly TextWriter _out;
    private readonly TextWriter _error;

    // `object`, not System.Threading.Lock: this type is reachable from the Coyote suites through
    // the pipeline's logger, and Coyote (1.7.11) rewrites Monitor.Enter/Exit but not
    // Lock.EnterScope. See AGENTS.md. Do not "modernize" it.
    private readonly object _gate = new();

    /// <summary>Creates a provider over <see cref="Console"/>, or over the writers the options name.</summary>
    public ParadiseConsoleLoggerProvider(ParadiseConsoleOptions? options = null)
    {
        _options = options ?? new ParadiseConsoleOptions();
        _out = _options.Out ?? Console.Out;
        _error = _options.Error ?? Console.Error;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        // The writers are the host's — Console.Out must outlive us, and an injected writer is the
        // test's to dispose. Nothing here owns anything.
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var writer = level >= _options.ErrorStreamThreshold ? _error : _out;
        var prefix = _options.IncludeCategory && category.Length > 0;

        // The prefix is WRITTEN rather than concatenated onto the message. `$"[{category}] {message}"`
        // copied the whole formatted message a second time, which measured as roughly half of this
        // path's allocation on a typical line — and it bought nothing, because everything between
        // the lock and its close is already atomic against other threads.
        lock (_gate)
        {
            if (prefix)
            {
                writer.Write('[');
                writer.Write(category);
                writer.Write("] ");
            }

            writer.WriteLine(message);
            if (exception is not null) writer.WriteLine(exception);
        }
    }

    /// <summary>
    /// Substitutes a message template's holes, giving <see cref="ParadiseConsoleOptions.RenderValue"/>
    /// first refusal on each argument.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns <see langword="null"/> when the caller's own formatter should be used instead —
    /// when no renderer is installed, when the state is not the name/value list that
    /// <c>FormattedLogValues</c> and the <c>[LoggerMessage]</c> generator both produce, or when the
    /// renderer declined every argument. That last check is what keeps the common case exactly
    /// correct: a message whose values the host has no opinion about is formatted by the code that
    /// owns the template, not by this re-implementation of it.
    /// </para>
    /// <para>
    /// MEL substitutes holes BY POSITION — a hole's name is a label for structured sinks, not a
    /// lookup key — so this walks holes and values in lockstep rather than matching names.
    /// </para>
    /// </remarks>
    private string? TryRender<TState>(TState state)
    {
        var render = _options.RenderValue;
        if (render is null) return null;
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values || values.Count == 0) return null;

        // The template lives in a trailing "{OriginalFormat}" entry; the preceding entries are the
        // arguments, in template order.
        string? template = null;
        for (var i = values.Count - 1; i >= 0; i--)
        {
            if (values[i].Key == "{OriginalFormat}")
            {
                template = values[i].Value as string;
                break;
            }
        }
        if (template is null) return null;

        // ONE pass over the arguments, keeping what the renderer said. The previous shape asked
        // the renderer once to find out whether anything was claimed and then AGAIN for each hole
        // while substituting — so a four-argument message invoked host code five times and threw
        // the first answer away. That is not merely wasted work: RenderValue is arbitrary host
        // code, and the CLI's calls ConvertPathToInternal, so the duplicate was a real path
        // conversion per path per message.
        var count = values.Count;
        var rendered = new string?[count];
        var claimed = false;
        for (var i = 0; i < count; i++)
        {
            if (values[i].Key == "{OriginalFormat}") continue;
            rendered[i] = render(values[i].Value);
            claimed |= rendered[i] is not null;
        }
        if (!claimed) return null;

        var builder = new StringBuilder(template.Length + 32);
        var argument = 0;
        var run = 0; // start of the literal text not yet appended

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c != '{' && c != '}') continue;

            // Literal text goes in RUNS, not a char at a time: Append(string, start, count) is one
            // copy where the per-char loop was one call per character, and templates are mostly
            // literal.
            if (i > run) builder.Append(template, run, i - run);

            if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}') i++;
                builder.Append('}');
                run = i + 1;
                continue;
            }

            if (i + 1 < template.Length && template[i + 1] == '{')
            {
                builder.Append('{');
                i++;
                run = i + 1;
                continue;
            }

            var close = template.IndexOf('}', i + 1);
            if (close < 0)
            {
                // Unterminated hole: emit the rest verbatim rather than throwing out of a log call.
                builder.Append(template, i, template.Length - i);
                return builder.ToString();
            }

            // "{Name}", "{Name:format}", "{Name,alignment}". Alignment is parsed only so it does
            // not land in the output; no engine template uses one.
            var hole = template.AsSpan(i + 1, close - i - 1);
            var colon = hole.IndexOf(':');
            var format = colon >= 0 ? hole[(colon + 1)..].ToString() : null;

            AppendValue(builder, values, rendered, ref argument, format);
            i = close;
            run = i + 1;
        }

        if (run < template.Length) builder.Append(template, run, template.Length - run);
        return builder.ToString();
    }

    private static void AppendValue(
        StringBuilder builder,
        IReadOnlyList<KeyValuePair<string, object?>> values,
        string?[] rendered,
        ref int argument,
        string? format)
    {
        // More holes than arguments is a malformed template; leave the excess empty rather than
        // throwing out of a logging call.
        if (argument >= values.Count) return;

        var index = argument++;
        if (rendered[index] is { } claimed)
        {
            builder.Append(claimed);
            return;
        }

        var value = values[index].Value;
        if (format is not null && value is IFormattable formattable)
        {
            builder.Append(formattable.ToString(format, CultureInfo.InvariantCulture));
            return;
        }

        builder.Append(value);
    }

    private sealed class Logger(ParadiseConsoleLoggerProvider provider, string category) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= provider._options.MinLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            ArgumentNullException.ThrowIfNull(formatter);

            var message = provider.TryRender(state) ?? formatter(state, exception);
            provider.Write(logLevel, category, message, exception);
        }

        /// <summary>Scopes are not supported; this returns a disposable that does nothing.</summary>
        /// <remarks>A scope is a per-message property bag for a structured sink. This one renders
        /// a line of text, and the engine opens no scopes. A host that wants them wants a real
        /// structured sink behind the same <see cref="ILogger"/>, not this.</remarks>
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        private NullScope() { }
        public void Dispose() { }
    }
}
