using System.Numerics;

using Paradise.BLOB;

namespace Paradise.Animation.Offline;

/// <summary>Turns a clip into deltas from a reference pose for additive layers: ozz's <c>AdditiveAnimationBuilder</c>, compiled into an <see cref="AdditiveAnimationBlob"/>.</summary>
/// <remarks>A translation delta subtracts the reference, a rotation delta is <c>conjugate(reference) × rotation</c> and a
/// scale delta divides by the reference, so <see cref="JointPoses.ApplyAdditive"/> at weight 1 on the reference pose
/// reproduces the source. A channel without keys stays empty and compiles to its identity delta.</remarks>
public static class AdditiveAnimationBuilder
{
    /// <summary>Deltas against each channel's first key — ozz's default reference, the pose the clip starts in.</summary>
    /// <exception cref="ArgumentException">The raw clip is invalid, or a first scale key has a zero component.</exception>
    public static NativeBlobAssetReference<AdditiveAnimationBlob> Build(RawAnimation source, float iframeInterval = 0f)
    {
        ArgumentNullException.ThrowIfNull(source);
        var reference = new JointPose[source.TrackCount];
        for (var track = 0; track < reference.Length; track++)
        {
            var keys = source.Tracks[track];
            reference[track] = new JointPose(
                keys.Translations.Count > 0 ? keys.Translations[0].Value : Vector3.Zero,
                keys.Rotations.Count > 0 ? keys.Rotations[0].Value : Quaternion.Identity,
                keys.Scales.Count > 0 ? keys.Scales[0].Value : Vector3.One);
        }

        return Build(source, reference, iframeInterval);
    }

    /// <summary>Deltas against <paramref name="reference"/>, one pose per track, such as the skeleton's rest poses or a frame of a base clip.</summary>
    /// <exception cref="ArgumentException">The raw clip is invalid, the reference is shorter than its tracks, or a reference pose is not finite, has a zero rotation or has a zero scale component.</exception>
    public static NativeBlobAssetReference<AdditiveAnimationBlob> Build(RawAnimation source, ReadOnlySpan<JointPose> reference, float iframeInterval = 0f)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsValid) throw new ArgumentException("The raw clip has a non-positive duration, too many tracks, or keys out of order or outside its duration.", nameof(source));
        if (reference.Length < source.TrackCount) throw new ArgumentException($"{reference.Length} reference poses for {source.TrackCount} tracks.", nameof(reference));

        var deltas = new RawAnimation { Name = source.Name, Duration = source.Duration };
        for (var track = 0; track < source.TrackCount; track++)
        {
            var (translation, inverseRotation, scale) = Reference(reference[track], track, nameof(reference));
            var keys = source.Tracks[track];
            var delta = new RawTrack();
            foreach (var key in keys.Translations) delta.Translations.Add(new TranslationKey(key.Time, key.Value - translation));
            foreach (var key in keys.Rotations) delta.Rotations.Add(new RotationKey(key.Time, inverseRotation * key.Value));
            foreach (var key in keys.Scales) delta.Scales.Add(new ScaleKey(key.Time, key.Value / scale));
            deltas.Tracks.Add(delta);
        }

        var builder = new StructBuilder<AdditiveAnimationBlob>();
        AnimationBuilder.Compile(deltas, iframeInterval, builder, ref builder.Value.Deltas);
        return builder.CreateNativeBlobAssetReference();
    }

    /// <summary>Deltas of a runtime clip against the pose it starts in, for a clip that arrives cooked.</summary>
    /// <remarks>Samples the clip at every key time, where its piecewise-linear tracks are exact, so the deltas follow
    /// the same curves; the result is quantized again when compiled.</remarks>
    /// <exception cref="ArgumentException">The clip's first pose has a zero scale component.</exception>
    public static NativeBlobAssetReference<AdditiveAnimationBlob> Build(ref AnimationBlob source, float iframeInterval = 0f) =>
        Build(Resample(ref source), iframeInterval);

    /// <summary>Deltas of a runtime clip against <paramref name="reference"/>, sampled at every key time as <see cref="Build(ref AnimationBlob, float)"/> does.</summary>
    /// <exception cref="ArgumentException">The reference is shorter than the clip's tracks, or a reference pose is not finite, has a zero rotation or has a zero scale component.</exception>
    public static NativeBlobAssetReference<AdditiveAnimationBlob> Build(ref AnimationBlob source, ReadOnlySpan<JointPose> reference, float iframeInterval = 0f) =>
        Build(Resample(ref source), reference, iframeInterval);

    private static (Vector3 Translation, Quaternion InverseRotation, Vector3 Scale) Reference(in JointPose pose, int track, string parameter)
    {
        var t = pose.Translation;
        var r = pose.Rotation;
        var s = pose.Scale;
        if (!float.IsFinite(t.X) || !float.IsFinite(t.Y) || !float.IsFinite(t.Z)) throw new ArgumentException($"Track {track}'s reference translation {t} is not finite.", parameter);
        var lengthSquared = r.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared == 0f) throw new ArgumentException($"Track {track}'s reference rotation {r} has no direction.", parameter);
        if (!float.IsFinite(s.X) || !float.IsFinite(s.Y) || !float.IsFinite(s.Z) || s.X == 0f || s.Y == 0f || s.Z == 0f)
        {
            throw new ArgumentException($"Track {track}'s reference scale {s} has a zero or non-finite component; a scale delta divides by it.", parameter);
        }

        return (t, Quaternion.Conjugate(Quaternion.Normalize(r)), s);
    }

    /// <summary>A key at every timepoint on every track. Each track is linear between the clip's timepoints, so these keys trace it exactly.</summary>
    private static RawAnimation Resample(ref AnimationBlob source)
    {
        var raw = new RawAnimation { Name = source.Name.ToString(), Duration = source.Duration };
        var trackCount = source.TrackCount;
        for (var track = 0; track < trackCount; track++) raw.Tracks.Add(new RawTrack());

        using var context = SamplingContext.Create(trackCount);
        using var pose = JointPoses.Create(trackCount);
        var ratios = source.Timepoints.ToArray();
        var previous = -1f;
        foreach (var ratio in ratios)
        {
            var time = MathF.Min(ratio * source.Duration, source.Duration);
            if (time <= previous) continue;
            previous = time;
            context.Value.Sample(ref source, ratio, ref pose.Value);
            for (var track = 0; track < trackCount; track++)
            {
                var sample = pose.Value[track];
                raw.Tracks[track].Translations.Add(new TranslationKey(time, sample.Translation));
                raw.Tracks[track].Rotations.Add(new RotationKey(time, sample.Rotation));
                raw.Tracks[track].Scales.Add(new ScaleKey(time, sample.Scale));
            }
        }

        return raw;
    }
}
