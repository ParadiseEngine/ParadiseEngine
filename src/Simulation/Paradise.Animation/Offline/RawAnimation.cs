using System.Numerics;

namespace Paradise.Animation.Offline;

/// <summary>The authoring-side clip: one track per skeleton joint, in skeleton order, each with keys sorted strictly by time inside 0..<see cref="Duration"/>; what <see cref="AnimationBuilder"/> compresses.</summary>
public sealed class RawAnimation
{
    public string Name { get; set; } = "";

    /// <summary>Seconds, positive.</summary>
    public float Duration { get; set; } = 1f;

    public List<RawTrack> Tracks { get; } = [];

    public int TrackCount => Tracks.Count;

    public bool IsValid
    {
        get
        {
            if (Duration <= 0f || Tracks.Count > SkeletonBlob.MaxJoints) return false;
            foreach (var track in Tracks)
            {
                if (!track.IsValid(Duration)) return false;
            }

            return true;
        }
    }
}

public sealed class RawTrack
{
    public List<TranslationKey> Translations { get; } = [];

    public List<RotationKey> Rotations { get; } = [];

    public List<ScaleKey> Scales { get; } = [];

    public bool IsValid(float duration) =>
        AreSorted(Translations.Select(k => k.Time), duration) &&
        AreSorted(Rotations.Select(k => k.Time), duration) &&
        AreSorted(Scales.Select(k => k.Time), duration);

    private static bool AreSorted(IEnumerable<float> times, float duration)
    {
        var previous = -1f;
        foreach (var time in times)
        {
            if (time < 0f || time > duration || time <= previous) return false;
            previous = time;
        }

        return true;
    }
}

public readonly record struct TranslationKey(float Time, Vector3 Value);

public readonly record struct RotationKey(float Time, Quaternion Value);

public readonly record struct ScaleKey(float Time, Vector3 Value);
