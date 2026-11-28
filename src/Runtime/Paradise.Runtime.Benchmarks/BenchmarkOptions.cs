using System.Globalization;
using Paradise.Features;

namespace Paradise.Runtime.Benchmarks;

internal enum BenchmarkWorkload { Reference, Showcase }

internal enum BenchmarkBackend { Null, WebGpu }

internal enum BenchmarkHost { Cooperative, Threaded }

/// <summary>Command-line options; see README.md.</summary>
internal sealed record BenchmarkOptions
{
    /// <summary>One tick: with the host's catch-up budget equal to the fixed step, every frame owes exactly
    /// one simulation tick however long it took, so per-frame numbers are deterministic.</summary>
    public static readonly TimeSpan LockstepStep = TimeSpan.FromTicks(1);

    public BenchmarkWorkload Workload { get; init; }
    public BenchmarkBackend Backend { get; init; }
    public BenchmarkHost Host { get; init; }
    public int Entities { get; init; } = 9;

    /// <summary>Entities despawned and replaced by new ones each tick, oldest first: projectiles, effects.</summary>
    public int Churn { get; init; }
    public int WarmupFrames { get; init; } = 300;
    public int Frames { get; init; } = 1200;
    public uint Width { get; init; } = 1280;
    public uint Height { get; init; } = 720;
    public bool ProbeFeatures { get; init; } = true;

    /// <summary>Showcase only: animate lights and settings by replacing records, as the samples do.</summary>
    public bool RecordUpdates { get; init; }
    public bool SampleTypes { get; init; }
    public int TopTypes { get; init; } = 30;
    public FeatureOverrides Features { get; init; } = FeatureOverrides.None;

    /// <summary>Most bytes a measured frame may allocate before the run exits with code 2, or null for no limit.</summary>
    /// <remarks>Under the cooperative host, where one thread runs the whole frame, every frame's thread-local
    /// bytes are checked, so an allocation on one frame in a thousand still fails. The threaded host has no
    /// frame on one thread, so there the process total must stay within the budget times the frames.</remarks>
    public long? Budget { get; init; }

    /// <summary>The simulation's step: lockstep under the cooperative host, 60 Hz on the threaded host's own thread.</summary>
    public TimeSpan FixedStep => Host == BenchmarkHost.Cooperative ? LockstepStep : TimeSpan.FromSeconds(1.0 / 60.0);

    public static BenchmarkOptions Parse(string[] args)
    {
        var options = new BenchmarkOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            options = name switch
            {
                "--workload" => options with { Workload = Enum<BenchmarkWorkload>(Value(args, ref i)) },
                "--backend" => options with { Backend = Enum<BenchmarkBackend>(Value(args, ref i)) },
                "--host" => options with { Host = Enum<BenchmarkHost>(Value(args, ref i)) },
                "--entities" => options with { Entities = Positive(Value(args, ref i), name) },
                "--churn" => options with { Churn = Positive(Value(args, ref i), name) },
                "--warmup" => options with { WarmupFrames = Positive(Value(args, ref i), name) },
                "--frames" => options with { Frames = Positive(Value(args, ref i), name) },
                "--width" => options with { Width = (uint)Positive(Value(args, ref i), name) },
                "--height" => options with { Height = (uint)Positive(Value(args, ref i), name) },
                "--top" => options with { TopTypes = Positive(Value(args, ref i), name) },
                "--features" => options with { Features = FeatureOverrides.Parse(Value(args, ref i)) },
                "--types" => options with { SampleTypes = true },
                "--no-probes" => options with { ProbeFeatures = false },
                "--record-updates" => options with { RecordUpdates = true },
                "--budget" => options with { Budget = NonNegative(Value(args, ref i), name) },
                _ => throw new FormatException($"Unknown option '{name}'. See README.md."),
            };
        }

        if (options.Churn > options.Entities) throw new FormatException("--churn cannot exceed --entities.");
        return options;
    }

    private static string Value(string[] args, ref int index)
    {
        if (index + 1 >= args.Length) throw new FormatException($"{args[index]} requires a value.");
        return args[++index];
    }

    private static T Enum<T>(string value) where T : struct, System.Enum =>
        System.Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && System.Enum.IsDefined(parsed)
            ? parsed
            : throw new FormatException($"'{value}' is not one of {string.Join(", ", System.Enum.GetNames<T>())}.");

    private static long NonNegative(string value, string name) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new FormatException($"{name} requires a non-negative integer.");

    private static int Positive(string value, string name) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new FormatException($"{name} requires a positive integer.");
}
