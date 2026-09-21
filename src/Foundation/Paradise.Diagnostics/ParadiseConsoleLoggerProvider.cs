using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

namespace Paradise.Diagnostics;

/// <summary>
/// An <see cref="ILoggerProvider"/> that writes engine diagnostics to a pair of text writers,
/// routing by level and rendering logged values through <see cref="ParadiseConsoleOptions.RenderValue"/>.
/// </summary>
/// <remarks>Loggers share a lock so concurrent callbacks cannot interleave output lines.</remarks>
public sealed class ParadiseConsoleLoggerProvider : ILoggerProvider
{
    private readonly ParadiseConsoleOptions _options;
    private readonly TextWriter _out;
    private readonly TextWriter _error;

    // Coyote 1.7.11 intercepts Monitor, but not System.Threading.Lock.EnterScope.
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
        // Writers belong to the host.
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var writer = level >= _options.ErrorStreamThreshold ? _error : _out;
        var prefix = _options.IncludeCategory && category.Length > 0;

        // Write the prefix separately to avoid copying the message again.
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
    /// Returns null when no renderer is installed, state is not structured, or no argument is claimed.
    /// Arguments are positional, matching FormattedLogValues. LoggerMessage handles repeated holes
    /// by parameter name instead; do not use repeated holes without adding support for that case.
    /// </remarks>
    private string? TryRender<TState>(TState state)
    {
        var render = _options.RenderValue;
        if (render is null) return null;
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values) return null;

        // Arguments precede the trailing OriginalFormat entry.
        string? template = null;
        var formatIndex = -1;
        for (var i = values.Count - 1; i >= 0; i--)
        {
            if (values[i].Key == "{OriginalFormat}")
            {
                template = values[i].Value as string;
                formatIndex = i;
                break;
            }
        }
        if (template is null) return null;

        // Invoke host rendering once per argument; callbacks may be expensive or stateful.
        var rendered = new string?[formatIndex];
        var claimed = false;
        for (var i = 0; i < formatIndex; i++)
        {
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

            // Copy literal runs in one append.
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
        // Exclude the OriginalFormat entry; unmatched holes must not append the template itself.
        // Malformed FormattedLogValues can still throw, as they do through the default formatter.
        if (argument >= rendered.Length) return;

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
        /// <remarks>Use a structured sink when scope properties are needed.</remarks>
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        private NullScope() { }
        public void Dispose() { }
    }
}
