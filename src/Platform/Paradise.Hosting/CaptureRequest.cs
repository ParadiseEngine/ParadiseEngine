namespace Paradise.Hosting;

/// <summary>Names exact rendered frames to capture and an optional continuing interval.</summary>
public sealed record CaptureRequest(string Path, IReadOnlyList<int> Frames, int? EveryFrames)
{
    public bool IsSequence => EveryFrames is not null || Frames.Count > 1;

    public IEnumerable<int> Schedule()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Path);
        ArgumentNullException.ThrowIfNull(Frames);
        if (EveryFrames is <= 0) throw new ArgumentOutOfRangeException(nameof(EveryFrames));
        var previous = 0;
        foreach (var frame in Frames)
        {
            if (frame <= previous)
                throw new ArgumentException("Capture frames must be positive and strictly increasing.", nameof(Frames));
            yield return frame;
            previous = frame;
        }
        if (EveryFrames is not { } step) yield break;
        for (var next = (long)previous + step; next <= int.MaxValue; next += step)
            yield return (int)next;
    }

    public string PathFor(int frame)
    {
        if (!IsSequence) return Path;
        var directory = System.IO.Path.GetDirectoryName(Path);
        var name = System.IO.Path.GetFileNameWithoutExtension(Path);
        var extension = System.IO.Path.GetExtension(Path);
        var numbered = $"{name}-{frame:D5}{extension}";
        return string.IsNullOrEmpty(directory) ? numbered : System.IO.Path.Combine(directory, numbered);
    }
}
