using Microsoft.Extensions.Logging;

namespace Paradise.Diagnostics;

/// <summary>Builds a console <see cref="ILoggerFactory"/> without the full Microsoft.Extensions.Logging stack.</summary>
/// <remarks>
/// <para>
/// <see cref="ILoggerFactory"/> is an interface in Microsoft.Extensions.Logging.Abstractions, but
/// the concrete <c>LoggerFactory</c> that normally implements it lives in
/// Microsoft.Extensions.Logging — a package that brings options, configuration and DI with it.
/// A host that wants one console and no filtering pipeline should not have to take all of that,
/// so this supplies the fifteen lines it would otherwise be using.
/// </para>
/// <para>
/// A host that already has that stack should ignore this and register
/// <see cref="ParadiseConsoleLoggerProvider"/> with its own factory — or ignore this package
/// entirely and put ZLogger or Serilog behind the same <see cref="ILogger"/>, which is the point
/// of the seam.
/// </para>
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
        /// <remarks>Silently accepting a provider it would never write to is the worse failure —
        /// a host that adds a file sink here and sees nothing in the file has no way to find out
        /// why. A host that needs two sinks needs the real <c>LoggerFactory</c>.</remarks>
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException(
            $"{nameof(ParadiseConsole)}.{nameof(CreateFactory)} builds a console-only factory. "
            + $"To combine sinks, use Microsoft.Extensions.Logging's LoggerFactory and register "
            + $"{nameof(ParadiseConsoleLoggerProvider)} with it.");

        public void Dispose() => provider.Dispose();
    }
}
