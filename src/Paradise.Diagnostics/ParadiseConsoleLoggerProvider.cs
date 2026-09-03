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
        var line = _options.IncludeCategory && category.Length > 0
            ? $"[{category}] {message}"
            : message;

        lock (_gate)
        {
            writer.WriteLine(line);
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

        var claimed = false;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].Key == "{OriginalFormat}") continue;
            if (render(values[i].Value) is not null) { claimed = true; break; }
        }
        if (!claimed) return null;

        var builder = new StringBuilder(template.Length + 32);
        var argument = 0;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];

            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{') { builder.Append('{'); i++; continue; }

                var close = template.IndexOf('}', i + 1);
                if (close < 0) { builder.Append(template, i, template.Length - i); break; }

                // "{Name}", "{Name:format}", "{Name,alignment}". Alignment is parsed only so it
                // does not land in the output; no engine template uses one.
                var hole = template.AsSpan(i + 1, close - i - 1);
                var colon = hole.IndexOf(':');
                var format = colon >= 0 ? hole[(colon + 1)..].ToString() : null;

                builder.Append(Format(values, ref argument, render, format));
                i = close;
                continue;
            }

            if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}') i++;
                builder.Append('}');
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string Format(
        IReadOnlyList<KeyValuePair<string, object?>> values,
        ref int argument,
        Func<object?, string?> render,
        string? format)
    {
        // More holes than arguments is a malformed template; leave the excess empty rather than
        // throwing out of a logging call.
        if (argument >= values.Count) return string.Empty;
        var value = values[argument++].Value;

        if (render(value) is { } rendered) return rendered;
        if (format is not null && value is IFormattable formattable)
            return formattable.ToString(format, CultureInfo.InvariantCulture);
        return value?.ToString() ?? string.Empty;
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
