using System.Numerics;

namespace Paradise.Animation.Offline;

/// <summary>
/// Compresses a <see cref="RawAnimation"/> into a runtime <see cref="AnimationClip"/>: keys of every
/// track merged into one time-sorted stream per component with back-links, values quantized,
/// i-frames for seeking. ozz's <c>AnimationBuilder</c>, producing the same bytes.
/// </summary>
/// <remarks>
/// Every arithmetic step keeps ozz's operand order (a multiply by the inverse duration rather than
/// a divide, its own float-to-half rounding) because the golden test compares bytes with an
/// archive ozz built, and a last-bit difference in a ratio would move keys between timepoints.
/// </remarks>
public static class AnimationBuilder
{
    private const int MaxPreviousOffset = ushort.MaxValue;
    private const float Sqrt2 = 1.4142135623730950488f;
    private const float Sqrt2Over2 = 0.70710678118654752440f;
    private const float QuaternionScale = (1 << 15) - 1;

    /// <param name="iframeInterval">Seconds between i-frames; 0 for none (the sampler then rewinds by walking the stream). A cooked clip uses a few seconds.</param>
    /// <exception cref="ArgumentException">The raw clip is invalid, or has more distinct key times than the format can index.</exception>
    public static AnimationClip Build(RawAnimation raw, float iframeInterval = 0f)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (!raw.IsValid) throw new ArgumentException("The raw clip has a non-positive duration, too many tracks, or keys out of order or outside its duration.", nameof(raw));

        var duration = raw.Duration;
        var inverseDuration = 1f / duration;
        var trackCount = raw.TrackCount;
        var padded = AnimationClip.PaddedTrackCount(trackCount);

        var translations = new List<SortingKey<Vector3>>();
        var rotations = new List<SortingKey<Quaternion>>();
        var scales = new List<SortingKey<Vector3>>();
        ushort track = 0;
        for (; track < trackCount; track++)
        {
            var source = raw.Tracks[track];
            CopyRaw(source.Translations.Select(k => (k.Time, k.Value)).ToList(), track, duration, Vector3.Zero, translations);
            CopyRaw(source.Rotations.Select(k => (k.Time, k.Value)).ToList(), track, duration, Quaternion.Identity, rotations);
            CopyRaw(source.Scales.Select(k => (k.Time, k.Value)).ToList(), track, duration, Vector3.One, scales);
        }

        for (; track < padded; track++)
        {
            PushIdentity(track, 0f, Vector3.Zero, translations);
            PushIdentity(track, duration, Vector3.Zero, translations);
            PushIdentity(track, 0f, Quaternion.Identity, rotations);
            PushIdentity(track, duration, Quaternion.Identity, rotations);
            PushIdentity(track, 0f, Vector3.One, scales);
            PushIdentity(track, duration, Vector3.One, scales);
        }

        FixupQuaternions(rotations);
        Sort(translations, padded, Vector3.Lerp);
        Sort(rotations, padded, LerpRotation);
        Sort(scales, padded, Vector3.Lerp);

        var timepoints = BuildTimepoints(translations, rotations, scales);
        if (timepoints.Length > ushort.MaxValue) throw new ArgumentException($"The clip has {timepoints.Length} distinct key times; the format indexes at most {ushort.MaxValue}.", nameof(raw));

        var translationStream = Compress(timepoints, translations, padded, iframeInterval, duration, CompressVector3);
        var rotationStream = Compress(timepoints, rotations, padded, iframeInterval, duration, CompressQuaternion);
        var scaleStream = Compress(timepoints, scales, padded, iframeInterval, duration, CompressVector3);

        var ratios = new float[timepoints.Length];
        for (var i = 0; i < ratios.Length; i++) ratios[i] = timepoints[i] * inverseDuration;

        return new AnimationClip(raw.Name, duration, trackCount, ratios, translationStream, rotationStream, scaleStream);
    }

    private struct SortingKey<T>
    {
        public ushort Track;
        public float PreviousKeyTime;
        public float Time;
        public T Value;
    }

    private static void PushIdentity<T>(ushort track, float time, T identity, List<SortingKey<T>> destination)
    {
        var previousTime = -1f;
        if (destination.Count > 0 && destination[^1].Track == track) previousTime = destination[^1].Time;
        destination.Add(new SortingKey<T> { Track = track, PreviousKeyTime = previousTime, Time = time, Value = identity });
    }

    /// <summary>Every track gets a key at 0 and at the duration, so the sampler never extrapolates; an empty track holds identity, not the rest pose — the caller fills that in.</summary>
    private static void CopyRaw<T>(List<(float Time, T Value)> source, ushort track, float duration, T identity, List<SortingKey<T>> destination)
    {
        if (source.Count == 0)
        {
            PushIdentity(track, 0f, identity, destination);
            PushIdentity(track, duration, identity, destination);
        }
        else if (source.Count == 1)
        {
            destination.Add(new SortingKey<T> { Track = track, PreviousKeyTime = -1f, Time = 0f, Value = source[0].Value });
            destination.Add(new SortingKey<T> { Track = track, PreviousKeyTime = 0f, Time = duration, Value = source[0].Value });
        }
        else
        {
            var previousTime = -1f;
            if (source[0].Time != 0f)
            {
                destination.Add(new SortingKey<T> { Track = track, PreviousKeyTime = previousTime, Time = 0f, Value = source[0].Value });
                previousTime = 0f;
            }

            foreach (var (time, value) in source)
            {
                destination.Add(new SortingKey<T> { Track = track, PreviousKeyTime = previousTime, Time = time, Value = value });
                previousTime = time;
            }

            if (source[^1].Time - duration != 0f)
            {
                destination.Add(new SortingKey<T> { Track = track, PreviousKeyTime = previousTime, Time = duration, Value = source[^1].Value });
            }
        }
    }

    /// <summary>Normalizes, and flips each key onto the same hemisphere as its predecessor so the sampler's lerp takes the short arc.</summary>
    private static void FixupQuaternions(List<SortingKey<Quaternion>> keys)
    {
        var track = -1;
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var normalized = NormalizeOrIdentity(key.Value);
            if (track != key.Track)
            {
                if (normalized.W < 0f) normalized = -normalized;
            }
            else
            {
                var previous = keys[i - 1].Value;
                if (Quaternion.Dot(previous, normalized) < 0f) normalized = -normalized;
            }

            key.Value = normalized;
            keys[i] = key;
            track = key.Track;
        }
    }

    private static Quaternion NormalizeOrIdentity(Quaternion q)
    {
        var lengthSquared = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
        if (lengthSquared == 0f) return Quaternion.Identity;
        var inverse = 1f / MathF.Sqrt(lengthSquared);
        return new Quaternion(q.X * inverse, q.Y * inverse, q.Z * inverse, q.W * inverse);
    }

    private static Quaternion LerpRotation(Quaternion a, Quaternion b, float t)
    {
        var dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        var target = dot < 0f ? -b : b;
        var lerp = new Quaternion(
            (target.X - a.X) * t + a.X,
            (target.Y - a.Y) * t + a.Y,
            (target.Z - a.Z) * t + a.Z,
            (target.W - a.W) * t + a.W);
        return NormalizeOrIdentity(lerp);
    }

    private static int Compare<T>(SortingKey<T> left, SortingKey<T> right)
    {
        var timeDifference = left.PreviousKeyTime - right.PreviousKeyTime;
        if (timeDifference < 0f) return -1;
        if (timeDifference == 0f) return left.Track.CompareTo(right.Track);
        return 1;
    }

    /// <summary>Sorts by previous-key time then track, and splits any track whose consecutive keys sit more than 65535 stream entries apart, since the back-link is 16-bit.</summary>
    private static void Sort<T>(List<SortingKey<T>> keys, int paddedTracks, Func<T, T, float, T> lerp)
    {
        Comparison<SortingKey<T>> comparison = Compare;
        keys.Sort(comparison);
        var comparer = Comparer<SortingKey<T>>.Create(comparison);
        var previouses = new (int Last, int Penultimate)[paddedTracks];
        for (var loop = true; loop;)
        {
            loop = false;
            Array.Fill(previouses, (-1, -1));
            for (var i = 0; i < keys.Count; i++)
            {
                var track = keys[i].Track;
                ref var previous = ref previouses[track];
                if (previous.Last != -1 && i - previous.Last > MaxPreviousOffset)
                {
                    var last = keys[previous.Last];
                    var penultimate = keys[previous.Penultimate];
                    var insert = new SortingKey<T>
                    {
                        Track = track,
                        PreviousKeyTime = penultimate.Time,
                        Time = (penultimate.Time + last.Time) * 0.5f,
                        Value = lerp(penultimate.Value, last.Value, 0.5f),
                    };
                    keys.RemoveAt(previous.Last);
                    last.PreviousKeyTime = insert.Time;
                    InsertSorted(keys, insert, comparer);
                    InsertSorted(keys, last, comparer);
                    loop = true;
                    break;
                }

                previous.Penultimate = previous.Last;
                previous.Last = i;
            }
        }
    }

    private static void InsertSorted<T>(List<SortingKey<T>> keys, SortingKey<T> key, IComparer<SortingKey<T>> comparer)
    {
        var at = keys.BinarySearch(key, comparer);
        keys.Insert(at < 0 ? ~at : at + 1, key);
    }

    private static float[] BuildTimepoints(List<SortingKey<Vector3>> translations, List<SortingKey<Quaternion>> rotations, List<SortingKey<Vector3>> scales)
    {
        var times = new List<float>(translations.Count + rotations.Count + scales.Count);
        foreach (var key in translations) times.Add(key.Time);
        foreach (var key in rotations) times.Add(key.Time);
        foreach (var key in scales) times.Add(key.Time);
        times.Sort();
        var unique = new List<float>(times.Count);
        foreach (var time in times)
        {
            if (unique.Count == 0 || unique[^1] != time) unique.Add(time);
        }

        return [.. unique];
    }

    private static KeyframeStream Compress<T>(float[] timepoints, List<SortingKey<T>> keys, int paddedTracks, float iframeInterval, float duration, Action<T, ushort[], int> compress)
    {
        var ratioBytes = timepoints.Length <= byte.MaxValue ? 1 : 2;
        var ratios = new byte[keys.Count * ratioBytes];
        var previouses = new ushort[keys.Count];
        var values = new ushort[keys.Count * 3];
        var lastOfTrack = new int[paddedTracks];
        Array.Fill(lastOfTrack, -1);
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var timepoint = Array.BinarySearch(timepoints, key.Time);
            if (timepoint < 0) throw new InvalidOperationException("A key's time is missing from the timepoints it was collected into.");
            if (ratioBytes == 1)
            {
                ratios[i] = (byte)timepoint;
            }
            else
            {
                ratios[i * 2] = (byte)timepoint;
                ratios[i * 2 + 1] = (byte)(timepoint >> 8);
            }

            var difference = lastOfTrack[key.Track] < 0 ? 0 : i - lastOfTrack[key.Track];
            previouses[i] = (ushort)difference;
            compress(key.Value, values, i * 3);
            lastOfTrack[key.Track] = i;
        }

        var (entries, desc, interval) = BuildIframes(keys, paddedTracks, iframeInterval, duration);
        return new KeyframeStream(ratios, previouses, values, entries, desc, interval);
    }

    private static (byte[] Entries, uint[] Desc, float Interval) BuildIframes<T>(List<SortingKey<T>> keys, int paddedTracks, float interval, float duration)
    {
        if (paddedTracks == 0 || interval <= 0f) return ([], [], 1f);

        var divisions = (int)MathF.Max(1f, duration / interval);
        var entries = new List<byte>();
        var desc = new List<uint>();
        for (var i = 0; i < divisions; i++)
        {
            var time = duration * (i + 1) / divisions;
            var snapshot = new uint[paddedTracks];
            var last = -1;
            for (var k = 0; k < keys.Count && keys[k].PreviousKeyTime <= time; k++)
            {
                snapshot[keys[k].Track] = (uint)k;
                last = k;
            }

            if (last <= paddedTracks * 2 - 1) continue;
            if (desc.Count > 0 && last <= desc[^1]) continue;
            desc.Add((uint)entries.Count);
            desc.Add((uint)last);
            entries.AddRange(GroupVarint.Encode(snapshot));
        }

        var actualIntervals = desc.Count / 2;
        return ([.. entries], [.. desc], entries.Count == 0 ? 1f : 1f / actualIntervals);
    }

    private static void CompressVector3(Vector3 value, ushort[] destination, int at)
    {
        destination[at] = HalfFloat.FromSingle(value.X);
        destination[at + 1] = HalfFloat.FromSingle(value.Y);
        destination[at + 2] = HalfFloat.FromSingle(value.Z);
    }

    /// <summary>The largest component is dropped and the other three quantized to 15 bits over −√2/2..√2/2; see <see cref="SamplingContext.ReadQuaternion"/> for the inverse.</summary>
    internal static void CompressQuaternion(Quaternion value, ushort[] destination, int at)
    {
        Span<float> q = [value.X, value.Y, value.Z, value.W];
        var largest = 0;
        for (var c = 1; c < 4; c++)
        {
            if (MathF.Abs(q[c]) > MathF.Abs(q[largest])) largest = c;
        }

        const float scale = QuaternionScale / Sqrt2;
        const float offset = -Sqrt2Over2;
        Span<int> component = stackalloc int[3];
        var slot = 0;
        for (var c = 0; c < 4; c++)
        {
            if (c == largest) continue;
            component[slot++] = Math.Min((int)((q[c] - offset) * scale + 0.5f), (int)QuaternionScale);
        }

        var sign = q[largest] < 0f ? 1 : 0;
        var packed = (ulong)(largest & 0x3) | (ulong)((sign & 0x1) << 2) | (ulong)(component[0] & 0x7fff) << 3
            | (ulong)(component[1] & 0x7fff) << 18 | ((ulong)component[2] & 0x7fff) << 33;
        destination[at] = (ushort)(packed & 0xffff);
        destination[at + 1] = (ushort)((packed >> 16) & 0xffff);
        destination[at + 2] = (ushort)((packed >> 32) & 0xffff);
    }
}
