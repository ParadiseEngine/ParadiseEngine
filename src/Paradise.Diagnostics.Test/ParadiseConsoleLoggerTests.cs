using Microsoft.Extensions.Logging;

namespace Paradise.Diagnostics.Test;

/// <summary>
/// The console sink, and the value-rendering seam that is the point of it (issue #232).
/// </summary>
/// <remarks>
/// A stand-in for <c>UPath</c> is used throughout rather than the real one: Zio would be a
/// dependency this package deliberately does not have, and the seam is defined over
/// <see cref="object"/> precisely so that the sink never learns what a path is.
/// </remarks>
public class ParadiseConsoleLoggerTests
{
    /// <summary>Stands in for a Zio <c>UPath</c>: a value whose own ToString is not what a person wants to read.</summary>
    private readonly record struct MountedPath(string Value)
    {
        public override string ToString() => Value;
    }

    private static (ILogger Logger, StringWriter Out, StringWriter Error) Sink(
        Func<object?, string?>? renderValue = null,
        bool includeCategory = true,
        LogLevel minLevel = LogLevel.Information,
        string category = "Test")
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var provider = new ParadiseConsoleLoggerProvider(new ParadiseConsoleOptions
        {
            MinLevel = minLevel,
            IncludeCategory = includeCategory,
            RenderValue = renderValue,
            Out = output,
            Error = error,
        });
        return (provider.CreateLogger(category), output, error);
    }

    [Test]
    public async Task a_host_renderer_replaces_the_value_a_library_logged()
    {
        // The whole issue in one test: the library logs the mounted path it was given, the host
        // turns it into something a person can paste into Explorer, and the library never learns
        // which filesystem it was handed.
        var (logger, output, _) = Sink(
            renderValue: value => value is MountedPath path ? $"C:\\proj{path.Value.Replace('/', '\\')}" : null);

        logger.LogInformation("minted: {Sidecar}", new MountedPath("/game/assets/crate.png.meta"));

        await Assert.That(output.ToString().Trim())
            .IsEqualTo(@"[Test] minted: C:\proj\game\assets\crate.png.meta");
    }

    [Test]
    public async Task a_message_the_renderer_declines_is_formatted_by_its_own_caller()
    {
        // The fast path, and the one that must stay exactly correct: when the host has no opinion
        // about any argument, the sink does NOT re-render the template, it uses the formatter the
        // logging call supplied. Anything else would be this class quietly reimplementing MEL's
        // formatting for every message in the engine.
        var (logger, output, _) = Sink(renderValue: _ => null);

        logger.LogInformation("swept {Count} file(s) from {Where}", 3, "build/");

        await Assert.That(output.ToString().Trim()).IsEqualTo("[Test] swept 3 file(s) from build/");
    }

    [Test]
    public async Task a_format_specifier_survives_a_message_that_also_carries_a_rendered_value()
    {
        // Re-rendering the template means re-implementing "{X:F2}" too. A message that mixes a
        // value the host claims with one it does not is where that gets forgotten.
        var (logger, output, _) = Sink(
            renderValue: value => value is MountedPath path ? $"<{path.Value}>" : null);

        logger.LogInformation("{Path} took {Seconds:F2}s", new MountedPath("/a/b"), 1.23456);

        await Assert.That(output.ToString().Trim()).IsEqualTo("[Test] </a/b> took 1.23s");
    }

    [Test]
    public async Task braces_in_a_template_are_not_holes()
    {
        var (logger, output, _) = Sink(renderValue: value => value is MountedPath ? "rendered" : null);

        logger.LogInformation("{{literal}} {Path}", new MountedPath("/x"));

        await Assert.That(output.ToString().Trim()).IsEqualTo("[Test] {literal} rendered");
    }

    [Test]
    [Arguments(LogLevel.Trace, false)]
    [Arguments(LogLevel.Debug, false)]
    [Arguments(LogLevel.Information, false)]
    [Arguments(LogLevel.Warning, true)]
    [Arguments(LogLevel.Error, true)]
    [Arguments(LogLevel.Critical, true)]
    public async Task severity_picks_the_stream(LogLevel level, bool expectedOnError)
    {
        // Severity used to be the convention "Console.Error for bad news". It stays one, but it is
        // now a level a host can move rather than a choice frozen at each call site.
        var (logger, output, error) = Sink(minLevel: LogLevel.Trace);

        logger.Log(level, "message");

        var landed = expectedOnError ? error : output;
        var empty = expectedOnError ? output : error;
        await Assert.That(landed.ToString().Trim()).IsEqualTo("[Test] message");
        await Assert.That(empty.ToString()).IsEqualTo("");
    }

    [Test]
    public async Task a_level_below_the_minimum_is_never_formatted()
    {
        var (logger, output, error) = Sink(minLevel: LogLevel.Warning);

        await Assert.That(logger.IsEnabled(LogLevel.Information)).IsFalse();
        logger.LogInformation("dropped");

        await Assert.That(output.ToString()).IsEqualTo("");
        await Assert.That(error.ToString()).IsEqualTo("");
    }

    [Test]
    public async Task the_category_prefix_is_the_hosts_choice()
    {
        // The CLI's pipeline lines were bare Console.WriteLine before the seam and must stay bare;
        // engine diagnostics want the category, because it is the only thing a reader can filter.
        var (bare, bareOut, _) = Sink(includeCategory: false, category: "Ignored");
        bare.LogInformation("minted: crate.png.meta");
        await Assert.That(bareOut.ToString().Trim()).IsEqualTo("minted: crate.png.meta");

        var (prefixed, prefixedOut, _) = Sink(includeCategory: true, category: "WebGPU");
        prefixed.LogInformation("device lost");
        await Assert.That(prefixedOut.ToString().Trim()).IsEqualTo("[WebGPU] device lost");
    }

    [Test]
    public async Task an_exception_is_written_under_its_message()
    {
        var (logger, _, error) = Sink();

        logger.LogError(new InvalidOperationException("boom"), "release callback threw");

        var written = error.ToString();
        await Assert.That(written).Contains("[Test] release callback threw");
        await Assert.That(written).Contains("boom");
    }
}
