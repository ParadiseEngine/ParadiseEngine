using System.Numerics;

namespace Paradise.Animation.Offline;

/// <summary>
/// Drops keys a linear interpolation of their neighbours reproduces within a tolerance, measured
/// where it matters: as world-space distance at the end of the joint's hierarchy, so a hip's
/// rotation is held to a tighter angle than a fingertip's. ozz's <c>AnimationOptimizer</c>.
/// </summary>
public static class AnimationOptimizer
{
    /// <param name="Tolerance">Metres of error allowed at <paramref name="Distance"/> from the joint.</param>
    /// <param name="Distance">How far from the joint the error is measured, metres; the joint's own hierarchy length is used when longer.</param>
    /// <remarks>No parameter defaults: <c>new Setting()</c> on a struct is all zeros, which is a tolerance that keeps nothing. Start from <see cref="Default"/>.</remarks>
    public readonly record struct Setting(float Tolerance, float Distance)
    {
        /// <summary>ozz's defaults: 1 mm of error measured 10 cm from the joint.</summary>
        public static Setting Default { get; } = new(1e-3f, 1e-1f);
    }

    /// <exception cref="ArgumentException">The clip is invalid or has a different track count than the skeleton has joints.</exception>
    public static RawAnimation Optimize(RawAnimation raw, ref SkeletonBlob skeleton, Setting setting, IReadOnlyDictionary<int, Setting>? jointOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (!raw.IsValid) throw new ArgumentException("The raw clip is invalid.", nameof(raw));
        if (raw.TrackCount != skeleton.JointCount) throw new ArgumentException($"The clip has {raw.TrackCount} tracks and the skeleton {skeleton.JointCount} joints.", nameof(raw));

        var parents = skeleton.Parents.ToArray();
        var specs = BuildHierarchy(raw, parents, setting, jointOverrides);
        var output = new RawAnimation { Name = raw.Name, Duration = raw.Duration };
        for (var i = 0; i < raw.TrackCount; i++)
        {
            var input = raw.Tracks[i];
            var track = new RawTrack();
            var jointLength = specs[i].Length;
            var parentScale = parents[i] != SkeletonBlob.NoParent ? specs[parents[i]].Scale : 1f;
            var tolerance = specs[i].Tolerance;

            track.Translations.AddRange(Decimate(input.Translations, k => k.Time, k => k.Value,
                (left, right, t) => new TranslationKey(t, Vector3.Lerp(left.Value, right.Value, Alpha(left.Time, right.Time, t))),
                (a, b) => (a - b).Length() * parentScale, Vector3.Zero, tolerance));
            track.Rotations.AddRange(Decimate(input.Rotations, k => k.Time, k => k.Value,
                (left, right, t) => new RotationKey(t, LerpRotation(left.Value, right.Value, Alpha(left.Time, right.Time, t))),
                (a, b) => RotationDistance(a, b, jointLength), Quaternion.Identity, tolerance));
            track.Scales.AddRange(Decimate(input.Scales, k => k.Time, k => k.Value,
                (left, right, t) => new ScaleKey(t, Vector3.Lerp(left.Value, right.Value, Alpha(left.Time, right.Time, t))),
                (a, b) => (a - b).Length() * jointLength, Vector3.One, tolerance));
            output.Tracks.Add(track);
        }

        return output;
    }

    private struct Spec
    {
        public float Length;
        public float Scale;
        public float Tolerance;
    }

    /// <summary>Forward: accumulate scale down the tree. Backward: the reach of each joint is the longest child chain, and its tolerance the tightest below it.</summary>
    private static Spec[] BuildHierarchy(RawAnimation raw, short[] parents, Setting setting, IReadOnlyDictionary<int, Setting>? overrides)
    {
        var count = parents.Length;
        var specs = new Spec[count];
        for (var joint = 0; joint < count; joint++)
        {
            var track = raw.Tracks[joint];
            var maxScale = 0f;
            if (track.Scales.Count != 0)
            {
                foreach (var key in track.Scales)
                {
                    maxScale = MathF.Max(maxScale, MathF.Max(MathF.Max(MathF.Abs(key.Value.X), MathF.Abs(key.Value.Y)), MathF.Abs(key.Value.Z)));
                }
            }
            else
            {
                maxScale = 1f;
            }

            specs[joint].Scale = maxScale;
            if (parents[joint] != SkeletonBlob.NoParent) specs[joint].Scale *= specs[parents[joint]].Scale;
            var jointSetting = overrides is not null && overrides.TryGetValue(joint, out var found) ? found : setting;
            specs[joint].Length = jointSetting.Distance * specs[joint].Scale;
            specs[joint].Tolerance = jointSetting.Tolerance;
        }

        for (var joint = count - 1; joint >= 0; joint--)
        {
            var parent = parents[joint];
            if (parent == SkeletonBlob.NoParent) continue;
            var maxLengthSquared = 0f;
            foreach (var key in raw.Tracks[joint].Translations) maxLengthSquared = MathF.Max(maxLengthSquared, key.Value.LengthSquared());
            var maxLength = MathF.Sqrt(maxLengthSquared);
            ref var parentSpec = ref specs[parent];
            parentSpec.Length = MathF.Max(parentSpec.Length, specs[joint].Length + maxLength * parentSpec.Scale);
            parentSpec.Tolerance = MathF.Min(parentSpec.Tolerance, specs[joint].Tolerance);
        }

        return specs;
    }

    private static float Alpha(float left, float right, float time) => (time - left) / (right - left);

    private static Quaternion LerpRotation(Quaternion a, Quaternion b, float t)
    {
        var dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        var target = dot < 0f ? -b : b;
        var lerp = new Quaternion((target.X - a.X) * t + a.X, (target.Y - a.Y) * t + a.Y, (target.Z - a.Z) * t + a.Z, (target.W - a.W) * t + a.W);
        var lengthSquared = lerp.LengthSquared();
        if (lengthSquared == 0f) return Quaternion.Identity;
        var inverse = 1f / MathF.Sqrt(lengthSquared);
        return new Quaternion(lerp.X * inverse, lerp.Y * inverse, lerp.Z * inverse, lerp.W * inverse);
    }

    /// <summary>Chord length swept at <paramref name="radius"/> by the angle between the two rotations.</summary>
    private static float RotationDistance(Quaternion a, Quaternion b, float radius)
    {
        var cosHalfAngle = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        var sinHalfAngle = MathF.Sqrt(1f - MathF.Min(1f, cosHalfAngle * cosHalfAngle));
        return 2f * sinHalfAngle * radius;
    }

    /// <summary>Ramer–Douglas–Peucker over the track, then trailing keys the identity already reproduces are dropped too.</summary>
    private static List<TKey> Decimate<TKey, TValue>(List<TKey> source, Func<TKey, float> time, Func<TKey, TValue> value, Func<TKey, TKey, float, TKey> lerp, Func<TValue, TValue, float> distance, TValue identity, float tolerance)
        where TKey : struct
    {
        var output = new List<TKey>();
        if (source.Count < 2)
        {
            output.AddRange(source);
        }
        else
        {
            var segments = new Stack<(int First, int Second)>();
            var included = new bool[source.Count];
            segments.Push((0, source.Count - 1));
            included[0] = true;
            included[^1] = true;
            while (segments.Count > 0)
            {
                var (first, second) = segments.Pop();
                var max = -1f;
                var candidate = first;
                for (var i = first + 1; i < second; i++)
                {
                    var test = source[i];
                    var error = distance(value(lerp(source[first], source[second], time(test))), value(test));
                    if (error > tolerance && error > max)
                    {
                        max = error;
                        candidate = i;
                    }
                }

                if (candidate != first)
                {
                    included[candidate] = true;
                    if (candidate - first > 1) segments.Push((first, candidate));
                    if (second - candidate > 1) segments.Push((candidate, second));
                }
            }

            for (var i = 0; i < source.Count; i++)
            {
                if (included[i]) output.Add(source[i]);
            }
        }

        while (output.Count > 0)
        {
            var lastKey = output.Count == 1;
            var penultimate = lastKey ? identity : value(output[^2]);
            if (distance(penultimate, value(output[^1])) > tolerance) break;
            output.RemoveAt(output.Count - 1);
        }

        return output;
    }
}
