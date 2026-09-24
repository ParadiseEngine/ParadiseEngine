using System.Globalization;
using Paradise.Windowing;

namespace Paradise.Hosting;

/// <summary>Reads common launch options while leaving game options to the application.</summary>
public static class HostCommandLine
{
    public const int DefaultHeadlessFrames = 120;

    public static HostOptions Parse(string[] args, WindowOptions window)
    {
        ArgumentNullException.ThrowIfNull(args);
        var headless = Array.IndexOf(args, "--headless") >= 0;
        var hold = Value(args, "--hold");
        if (hold is not null && !headless) throw new FormatException("--hold requires --headless.");
        var keys = new List<KeyboardKey>();
        if (hold is not null)
        {
            foreach (var name in hold.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Enum.TryParse<KeyboardKey>(name, true, out var key) || !Enum.IsDefined(key))
                    throw new FormatException($"--hold does not know the key '{name}'.");
                keys.Add(key);
            }
        }

        var limit = PositiveInteger(args, "--frames") ?? (headless ? DefaultHeadlessFrames : (int?)null);
        var path = Value(args, "--screenshot");
        var every = PositiveInteger(args, "--capture-every");
        var wanted = Value(args, "--capture-frame");
        if (path is null && (every is not null || wanted is not null))
            throw new FormatException("Capture frame options require --screenshot <path>.");
        CaptureRequest? capture = null;
        if (path is not null)
        {
            var frames = ParseFrames(wanted);
            if (frames.Count == 0 && every is null) frames = [limit ?? DefaultHeadlessFrames];
            if (limit is { } end && frames.Count > 0 && frames[^1] > end)
                throw new FormatException($"Capture frame {frames[^1]} is past --frames {end}.");
            if (limit is { } bounded && frames.Count == 0 && every > bounded)
                throw new FormatException("The capture interval exceeds the run's frame limit.");
            capture = new CaptureRequest(path, frames, every);
        }
        return new HostOptions { Window = window, Headless = headless, Hold = keys, FrameLimit = limit, Capture = capture };
    }

    public static IReadOnlyList<int> ParseFrames(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var frames = new SortedSet<int>();
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var frame) || frame <= 0)
                throw new FormatException($"Invalid capture frame '{part}'; expected positive frame numbers.");
            frames.Add(frame);
        }
        if (frames.Count == 0) throw new FormatException("--capture-frame requires at least one frame.");
        return [.. frames];
    }

    public static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (Array.IndexOf(args, name, index + 1) >= 0)
            throw new FormatException($"{name} was specified more than once.");
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new FormatException($"{name} requires a value.");
        return args[index + 1];
    }

    private static int? PositiveInteger(string[] args, string name)
    {
        var value = Value(args, name);
        if (value is null) return null;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result <= 0)
            throw new FormatException($"{name} requires a positive integer.");
        return result;
    }
}
