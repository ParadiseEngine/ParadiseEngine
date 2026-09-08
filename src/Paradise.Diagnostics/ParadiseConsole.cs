using Microsoft.Extensions.Logging;

namespace Paradise.Diagnostics;

/// <summary>Builds a console <see cref="ILoggerFactory"/> without the full Microsoft.Extensions.Logging stack.</summary>
/// <remarks>
/// Uses only logging abstractions, avoiding the options, configuration and DI dependencies of
/// LoggerFactory. Hosts with a logging stack can register ParadiseConsoleLoggerProvider directly.
/// </remarks>
public static class ParadiseConsole
{
    /// <summary>Creates a factory whose loggers all write to one console sink.</summary>
    public static ILoggerFactory CreateFactory(ParadiseConsoleOptions? options = null) =>
        new Factory(new ParadiseConsoleLoggerProvider(options));

    /// <summary>Creates a single logger under <paramref name="category"/>, for a host that needs exactly one.</summary>
    public static ILogger CreateLogger(string category, ParadiseConsoleOptions? options = null) =>
        new ParadiseConsoleLoggerProvider(options).CreateLogger(category);

    private sealed class Factory(ParadiseConsoleLoggerProvider provider) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

        /// <summary>Not supported: this factory is one console and nothing else.</summary>
        /// <remarks>Use LoggerFactory to combine multiple sinks.</remarks>
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException(
            $"{nameof(ParadiseConsole)}.{nameof(CreateFactory)} builds a console-only factory. "
            + $"To combine sinks, use Microsoft.Extensions.Logging's LoggerFactory and register "
            + $"{nameof(ParadiseConsoleLoggerProvider)} with it.");

        public void Dispose() => provider.Dispose();
    }
}
