using Microsoft.Extensions.Logging;

namespace Paradise.Diagnostics.Test;

/// <summary>Tests console routing and host argument rendering.</summary>
/// <remarks>A path stub avoids adding Zio to the logging package.</remarks>
public partial class ParadiseConsoleLoggerTests
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
        // The host renders mounted paths without exposing its filesystem to the logging library.
        var (logger, output, _) = Sink(
            renderValue: value => value is MountedPath path ? $"C:\\proj{path.Value.Replace('/', '\\')}" : null);

        logger.LogInformation("minted: {Sidecar}", new MountedPath("/game/assets/crate.png.meta"));

        await Assert.That(output.ToString().Trim())
            .IsEqualTo(@"[Test] minted: C:\proj\game\assets\crate.png.meta");
    }

    [Test]
    public async Task a_message_the_renderer_declines_is_formatted_by_its_own_caller()
    {
        // When no argument is claimed, preserve the caller's formatter exactly.
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
    public async Task literal_text_after_the_last_hole_survives()
    {
        // Flush the literal run after the final hole.
        var (logger, output, _) = Sink(
            renderValue: value => value is MountedPath path ? $"<{path.Value}>" : null);

        logger.LogInformation(
            "kept: {Destination} already holds {Guid}; dropped by rename",
            new MountedPath("/a/b"), "1111");

        await Assert.That(output.ToString().Trim())
            .IsEqualTo("[Test] kept: </a/b> already holds 1111; dropped by rename");
    }

    [Test]
    public async Task a_renderer_is_asked_about_each_argument_exactly_once()
    {
        // Invoke the host renderer once per argument, including expensive path conversions.
        var asked = new List<object?>();
        var (logger, _, _) = Sink(renderValue: value =>
        {
            asked.Add(value);
            return value is MountedPath path ? $"<{path.Value}>" : null;
        });

        logger.LogInformation(
            "kept: {Destination} already holds {Guid}; dropped {Source}",
            new MountedPath("/a"), "1111", new MountedPath("/b"));

        await Assert.That(asked.Count).IsEqualTo(3);
    }

    [Test]
    public async Task a_hole_with_no_argument_behind_it_appends_nothing_not_the_template()
    {
        // Surplus holes must not consume OriginalFormat as an argument.
        // Use custom state: FormattedLogValues throws while reading a missing argument.
        var (logger, output, _) = Sink(
            renderValue: value => value is MountedPath path ? $"<{path.Value}>" : null);

        var state = new StubState("first={A} second={B}", new MountedPath("/x"));
        logger.Log(LogLevel.Information, default, state, null, static (s, _) => s.ToString()!);

        await Assert.That(output.ToString().Trim()).IsEqualTo("[Test] first=</x> second=");
    }

    /// <summary>A log state shaped like MEL's, but tolerant of being read past its arguments.</summary>
    private sealed class StubState(string template, params object?[] arguments)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public int Count => arguments.Length + 1;

        public KeyValuePair<string, object?> this[int index] => index == arguments.Length
            ? new KeyValuePair<string, object?>("{OriginalFormat}", template)
            : new KeyValuePair<string, object?>($"Arg{index}", arguments[index]);

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return this[i];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => template;
    }

    /// <summary>Verifies rendering with generated LoggerMessage state.</summary>
    /// <remarks>Generated state indexes parameters; FormattedLogValues indexes holes. Both must
    /// format correctly when a host renderer claims an argument.</remarks>
    private static partial class Generated
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "kept: {Destination} already holds {Guid}; dropped {Source}")]
        public static partial void Kept(ILogger logger, MountedPath destination, string guid, MountedPath source);
    }

    [Test]
    public async Task a_generated_logger_message_renders_through_the_same_seam()
    {
        var (logger, output, _) = Sink(
            renderValue: value => value is MountedPath path ? $"<{path.Value}>" : null);

        Generated.Kept(logger, new MountedPath("/a/b"), "1111", new MountedPath("/c/d"));

        await Assert.That(output.ToString().Trim())
            .IsEqualTo("[Test] kept: </a/b> already holds 1111; dropped </c/d>");
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
        // CLI progress can omit categories; engine diagnostics include them.
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
