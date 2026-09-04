using System.Numerics;

namespace Paradise.Animation.Offline;

/// <summary>
/// Turns a <see cref="ClipData"/> into the <see cref="RawAnimation"/> the builder compresses:
/// one track per skeleton joint, unanimated components holding the rest pose, STEP channels
/// baked into held keys, and wide rotation arcs subdivided — ozz interpolates every key
/// linearly (normalized lerp for rotations), where glTF means slerp.
/// </summary>
public static class ClipConverter
{
    /// <summary>ozz refuses a zero duration; a pose exported as one key at t=0 gets this, and samples to the pose at any ratio.</summary>
    public const float MinimumDuration = 1e-3f;

    /// <summary>How long before the next key a STEP channel's hold ends; a step then lands within a millisecond of where the source put it.</summary>
    public const float StepLead = 1e-3f;

    /// <summary>Widest rotation arc between two consecutive keys before slerped keys are inserted: normalized lerp strays under 0.005° from slerp at 15°, and about 1° at 90°.</summary>
    public const float MaxArcRadians = 15f * MathF.PI / 180f;

    /// <exception cref="ArgumentException">A channel names a joint the skeleton lacks, or its keys are not strictly ascending.</exception>
    public static RawAnimation ToRaw(ClipData clip, ref SkeletonBlob skeleton)
    {
        ArgumentNullException.ThrowIfNull(clip);

        var duration = MathF.Max(clip.Duration, MinimumDuration);
        var raw = new RawAnimation { Name = clip.Name, Duration = duration };
        var jointCount = skeleton.JointCount;
        for (var joint = 0; joint < jointCount; joint++)
        {
            var rest = skeleton.RestPoses[joint];
            var track = new RawTrack();
            track.Translations.Add(new TranslationKey(0f, rest.Translation));
            track.Rotations.Add(new RotationKey(0f, rest.Rotation));
            track.Scales.Add(new ScaleKey(0f, rest.Scale));
            raw.Tracks.Add(track);
        }

        foreach (var channel in clip.Channels)
        {
            if (channel.Joint < 0 || channel.Joint >= jointCount)
            {
                throw new ArgumentException($"Clip '{clip.Name}' animates joint {channel.Joint}; the skeleton has {jointCount}.", nameof(clip));
            }

            if (channel.Values.Length != channel.Times.Length * channel.FloatsPerKey)
            {
                throw new ArgumentException($"Clip '{clip.Name}' joint {channel.Joint} {channel.Path} has {channel.Times.Length} keys and {channel.Values.Length} values.", nameof(clip));
            }

            if (channel.Times.Length == 0) continue;
            CheckAscending(channel, duration, clip.Name);
            var track = raw.Tracks[channel.Joint];
            switch (channel.Path)
            {
                case ChannelPath.Translation:
                    track.Translations.Clear();
                    foreach (var (time, at) in Held(channel)) track.Translations.Add(new TranslationKey(time, Vector3(channel.Values, at)));
                    break;
                case ChannelPath.Rotation:
                    track.Rotations.Clear();
                    track.Rotations.AddRange(channel.Step ? Held(channel).Select(k => new RotationKey(k.Time, Rotation(channel.Values, k.At))) : Subdivided(channel));
                    break;
                case ChannelPath.Scale:
                    track.Scales.Clear();
                    foreach (var (time, at) in Held(channel)) track.Scales.Add(new ScaleKey(time, Vector3(channel.Values, at)));
                    break;
                default:
                    throw new ArgumentException($"Clip '{clip.Name}' animates an unknown path {(int)channel.Path}.", nameof(clip));
            }
        }

        return raw;
    }

    private static void CheckAscending(ClipChannelData channel, float duration, string clipName)
    {
        var previous = -1f;
        foreach (var time in channel.Times)
        {
            if (time <= previous || time < 0f || time > duration)
            {
                throw new ArgumentException($"Clip '{clipName}' joint {channel.Joint} {channel.Path} keys are not strictly ascending within 0..{duration}.", nameof(channel));
            }

            previous = time;
        }
    }

    /// <summary>(time, value offset) per emitted key; a STEP key is followed by a copy of itself just before the next key.</summary>
    private static IEnumerable<(float Time, int At)> Held(ClipChannelData channel)
    {
        var stride = channel.FloatsPerKey;
        for (var k = 0; k < channel.Times.Length; k++)
        {
            var time = channel.Times[k];
            yield return (time, k * stride);
            if (!channel.Step || k + 1 >= channel.Times.Length) continue;

            var next = channel.Times[k + 1];
            var hold = next - time > 2f * StepLead ? next - StepLead : (time + next) * 0.5f;
            if (hold > time && hold < next) yield return (hold, k * stride);
        }
    }

    /// <summary>Linear rotation keys, with slerped keys inserted wherever consecutive keys are more than <see cref="MaxArcRadians"/> apart.</summary>
    private static IEnumerable<RotationKey> Subdivided(ClipChannelData channel)
    {
        for (var k = 0; k < channel.Times.Length; k++)
        {
            var time = channel.Times[k];
            var rotation = Rotation(channel.Values, k * 4);
            yield return new RotationKey(time, rotation);
            if (k + 1 >= channel.Times.Length) continue;

            var nextTime = channel.Times[k + 1];
            var next = Rotation(channel.Values, (k + 1) * 4);
            var arc = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(Quaternion.Dot(rotation, next))));
            var pieces = (int)MathF.Ceiling(arc / MaxArcRadians);
            for (var piece = 1; piece < pieces; piece++)
            {
                var t = (float)piece / pieces;
                var at = time + (nextTime - time) * t;
                if (at <= time || at >= nextTime) continue;
                yield return new RotationKey(at, Quaternion.Slerp(rotation, next, t));
            }
        }
    }

    private static Vector3 Vector3(float[] values, int at) => new(values[at], values[at + 1], values[at + 2]);

    private static Quaternion Rotation(float[] values, int at) => Quaternion.Normalize(new Quaternion(values[at], values[at + 1], values[at + 2], values[at + 3]));
}
