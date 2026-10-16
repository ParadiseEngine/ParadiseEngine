using System.Globalization;

using Paradise.Assets.Pipeline;

using Zio;

namespace Paradise.Cli;

/// <summary>What the tray shows of a running rebuild: an estimated <paramref name="Fraction"/>
/// done (0..1) and the step the build reported last.</summary>
internal readonly record struct WatchProgress(double Fraction, BuildStage Stage, int Done, int Total, string? Current);

/// <summary>Estimates rebuild progress from stage durations and the latest build report.</summary>
/// <remarks>
/// Reports arrive on the build thread; snapshots may be read on any thread, including a timer
/// that refreshes the tray during a long-running step.
///
/// Once <see cref="MinimumHistory"/> asset walks are recorded, each stage is weighted by its
/// lower-median duration across the last <see cref="History"/> walks. This limits the influence
/// of cold builds on typically incremental rebuilds. Within a stage, elapsed time determines
/// progress, capped below completion until the next stage begins. Without enough history,
/// <see cref="DefaultWeights"/> and completed-step counts provide the estimate.
///
/// Progress never moves backwards within a rebuild. Unreached stages are excluded from their
/// stage's median, and optional file storage preserves history across watcher sessions.
/// </remarks>
internal sealed class RebuildProgress
{
    /// <summary>Stage weights before any rebuild has finished: verify and the asset walk dominate.</summary>
    internal static readonly double[] DefaultWeights = [0.05, 0.25, 0.65, 0.05];

    /// <summary>Time held back from a stage's time-based share, so a slower run than the last one
    /// waits near the end of its stage instead of claiming the next one.</summary>
    private const double TimeShareCeiling = 0.95;

    /// <summary>How many rebuilds the typical stage durations are taken over.</summary>
    internal const int History = 5;

    /// <summary>Requires enough samples to avoid using a single cold build as the typical duration.</summary>
    internal const int MinimumHistory = 2;

    /// <summary>How a stage the rebuild never reached is written in the store.</summary>
    private const string Unreached = "-";

    private static readonly int StageCount = Enum.GetValues<BuildStage>().Length;

    private readonly TimeProvider _time;
    private readonly IFileSystem? _fileSystem;
    private readonly UPath _store;
    private readonly object _gate = new();
    private readonly long[] _stageStarts = new long[StageCount];
    private readonly bool[] _stageSeen = new bool[StageCount];
    private readonly Queue<double[]> _history = new();
    private double[]? _typical;
    private BuildProgress? _latest;
    private double _fraction;

    /// <param name="fileSystem">Where <paramref name="store"/> lives; with neither, the history lasts as long as this object.</param>
    /// <param name="store">A file holding the history, one rebuild's stage durations in seconds per line; read now, rewritten after each rebuild that teaches.</param>
    public RebuildProgress(TimeProvider time, IFileSystem? fileSystem = null, UPath store = default)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        if (fileSystem is null || store.IsNull) return;

        _fileSystem = fileSystem;
        _store = store;
        foreach (var durations in Load(fileSystem, store))
        {
            if (_history.Count == History) _history.Dequeue();
            _history.Enqueue(durations);
        }

        _typical = Typical();
    }

    /// <summary>Starts a rebuild; what the previous one learned about stage durations is kept.</summary>
    public void Begin()
    {
        lock (_gate)
        {
            Array.Clear(_stageSeen);
            _latest = null;
            _fraction = 0;
        }
    }

    /// <summary>Records where the build is; true when this report starts a new stage.</summary>
    public bool Report(BuildProgress report)
    {
        lock (_gate)
        {
            var stageChanged = _latest?.Stage != report.Stage;
            if (stageChanged)
            {
                var index = (int)report.Stage;
                _stageStarts[index] = _time.GetTimestamp();
                _stageSeen[index] = true;
            }

            _latest = report;
            return stageChanged;
        }
    }

    /// <summary>The estimate now, for the latest report; null before the first one.</summary>
    public WatchProgress? Snapshot()
    {
        lock (_gate)
        {
            if (_latest is not { } latest) return null;
            _fraction = Math.Max(_fraction, Estimate(latest, _time.GetTimestamp()));
            return new WatchProgress(_fraction, latest.Stage, latest.Done, latest.Total, latest.Current);
        }
    }

    /// <summary>Records stage durations when the rebuild reached the asset walk.</summary>
    /// <remarks>Verification-only failures would bias later estimates toward short builds.
    /// Asset walks that fail still contribute timing; unreached stages do not.</remarks>
    public void End()
    {
        lock (_gate)
        {
            if (!_stageSeen[(int)BuildStage.Assets]) return;

            var now = _time.GetTimestamp();
            var durations = new double[StageCount];
            for (var stage = 0; stage < StageCount; stage++)
            {
                // A stage the rebuild never reached (Finish, after a walk that ended in errors)
                // took no time only because it did not run; it is left out of that stage's median.
                if (!_stageSeen[stage])
                {
                    durations[stage] = double.NaN;
                    continue;
                }

                var next = NextSeen(stage);
                var end = next < 0 ? now : _stageStarts[next];
                durations[stage] = _time.GetElapsedTime(_stageStarts[stage], end).TotalSeconds;
            }

            if (durations.Where(double.IsFinite).Sum() <= 0) return;
            if (_history.Count == History) _history.Dequeue();
            _history.Enqueue(durations);
            _typical = Typical();
            Save();
        }
    }

    private double Estimate(BuildProgress report, long now)
    {
        var weights = _typical ?? DefaultWeights;
        var total = weights.Sum();
        if (total <= 0) return 0;

        var stage = (int)report.Stage;
        var before = 0.0;
        for (var earlier = 0; earlier < stage; earlier++) before += weights[earlier];

        double within;
        if (_typical is null)
        {
            within = report.Total > 0 ? Math.Clamp((double)report.Done / report.Total, 0, 1) : 0;
        }
        else
        {
            var spent = _time.GetElapsedTime(_stageStarts[stage], now).TotalSeconds;
            within = weights[stage] > 0 ? Math.Min(spent / weights[stage], TimeShareCeiling) : 0;
        }

        return Math.Clamp((before + weights[stage] * within) / total, 0, 1);
    }

    /// <summary>The recorded rebuilds, oldest first; a line that is not one duration per stage is
    /// skipped, and an unreadable file is no history rather than a watcher that will not start.</summary>
    private static List<double[]> Load(IFileSystem fileSystem, UPath store)
    {
        var loaded = new List<double[]>();
        try
        {
            if (!fileSystem.FileExists(store)) return loaded;
            foreach (var line in fileSystem.ReadAllLines(store))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != StageCount) continue;
                var durations = new double[StageCount];
                var valid = true;
                for (var stage = 0; stage < StageCount && valid; stage++)
                {
                    if (fields[stage] == Unreached)
                    {
                        durations[stage] = double.NaN;
                        continue;
                    }

                    valid = double.TryParse(fields[stage], NumberStyles.Float, CultureInfo.InvariantCulture, out durations[stage])
                        && double.IsFinite(durations[stage]) && durations[stage] >= 0;
                }

                if (valid) loaded.Add(durations);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            loaded.Clear();
        }

        return loaded;
    }

    /// <summary>Best effort: a history that cannot be written costs the next session's first
    /// estimate, not the rebuild.</summary>
    private void Save()
    {
        if (_fileSystem is null) return;
        try
        {
            var directory = _store.GetDirectory();
            if (!_fileSystem.DirectoryExists(directory)) _fileSystem.CreateDirectory(directory);
            _fileSystem.WriteAllText(_store, string.Concat(_history.Select(durations =>
                string.Join(' ', durations.Select(seconds => double.IsNaN(seconds) ? Unreached : seconds.ToString("R", CultureInfo.InvariantCulture))) + "\n")));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Returns lower-median stage durations once enough rebuilds have been recorded.</summary>
    /// <remarks>For two samples the shorter wins, favoring incremental builds over cold runs.
    /// A stage that no recorded rebuild reached has zero weight.</remarks>
    private double[]? Typical()
    {
        if (_history.Count < MinimumHistory) return null;

        var typical = new double[StageCount];
        for (var stage = 0; stage < StageCount; stage++)
        {
            var samples = _history.Select(durations => durations[stage]).Where(double.IsFinite).Order().ToArray();
            typical[stage] = samples.Length == 0 ? 0 : samples[(samples.Length - 1) / 2];
        }

        return typical;
    }

    private int NextSeen(int stage)
    {
        for (var next = stage + 1; next < StageCount; next++)
        {
            if (_stageSeen[next]) return next;
        }

        return -1;
    }
}
