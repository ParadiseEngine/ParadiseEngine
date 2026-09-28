using System.Globalization;

using Paradise.Assets.Pipeline;

using Zio;

namespace Paradise.Cli;

/// <summary>What the tray shows of a running rebuild: an estimated <paramref name="Fraction"/>
/// done (0..1) and the step the build reported last.</summary>
internal readonly record struct WatchProgress(double Fraction, BuildStage Stage, int Done, int Total, string? Current);

/// <summary>
/// Turns a rebuild's <see cref="BuildProgress"/> reports into an estimated fraction done.
/// Reports arrive on the building thread; <see cref="Snapshot"/> is read from any thread, so a
/// caller can refresh on a timer while one step (a model conversion, say) takes a minute.
/// </summary>
/// <remarks>
/// A build reports thousands of steps, most of them index reuses that take microseconds, and a
/// few cooks that take seconds; a count alone would race to 90% and sit there. So each stage is
/// weighted by how long it typically took: the lower median of its duration over the last
/// <see cref="History"/> rebuilds that walked the assets. A median, because one cold rebuild (an
/// eleven-minute walk on ShiningPie against a one-second incremental one) would otherwise leave
/// the next rebuild stuck near zero and jumping at the end. Within a stage, the time spent against
/// that typical duration (held short of the end), not the step count: a stage's time sits in a few
/// slow steps (ShiningPie's verify spends half of it on the last sixth of its files), and that
/// profile repeats from one rebuild to the next. Before any history, <see cref="DefaultWeights"/>
/// and the step count. The fraction never moves backwards within a rebuild; a stage that ends
/// sooner than usual moves it forward to the next stage at once.
///
/// The history is kept in a small file under <c>.editor/</c> when given one, because a watcher
/// lives as long as a Blender session and its first rebuild is the one most often watched.
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

        if (_history.Count > 0) _typical = Typical();
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

    /// <summary>Ends a rebuild. Only one whose asset walk ran teaches the next estimate: a build
    /// refused at verify never walked the assets, and its short durations would make the next
    /// full build look stuck at the start. A walk that ended in errors still teaches; it took
    /// the time a walk takes, and a project mid-edit may fail every rebuild for a while.</summary>
    public void End()
    {
        lock (_gate)
        {
            if (!_stageSeen[(int)BuildStage.Assets]) return;

            var now = _time.GetTimestamp();
            var durations = new double[StageCount];
            for (var stage = 0; stage < StageCount; stage++)
            {
                if (!_stageSeen[stage]) continue;
                var next = NextSeen(stage);
                var end = next < 0 ? now : _stageStarts[next];
                durations[stage] = _time.GetElapsedTime(_stageStarts[stage], end).TotalSeconds;
            }

            if (durations.Sum() <= 0) return;
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
                string.Join(' ', durations.Select(seconds => seconds.ToString("R", CultureInfo.InvariantCulture))) + "\n")));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Per stage, the lower median of the recorded durations: with two samples, the
    /// shorter, since watch rebuilds are mostly incremental and an overestimate is what leaves the
    /// bar stuck.</summary>
    private double[] Typical()
    {
        var typical = new double[StageCount];
        var samples = new double[_history.Count];
        for (var stage = 0; stage < StageCount; stage++)
        {
            var i = 0;
            foreach (var durations in _history) samples[i++] = durations[stage];
            Array.Sort(samples);
            typical[stage] = samples[(samples.Length - 1) / 2];
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
