using System.Numerics;

namespace Paradise.Animation;

/// <summary>
/// Samples an <see cref="AnimationClip"/> at a ratio of its duration into one local pose per track.
/// Holds the per-track cursor ozz's sampler keeps between calls: stepping forward in time from
/// the last sample visits only the keys that passed, and a seek restarts from the nearest i-frame.
/// Allocate one per playing instance and keep using it for that instance.
/// </summary>
/// <remarks>
/// A scalar port of ozz-animation's <c>SamplingJob</c> (0.17). Where ozz uses estimated reciprocal
/// and inverse square root instructions, this uses exact division and square root; the poses
/// differ from native ozz at the fourth decimal and from the source clip by the quantization alone.
/// </remarks>
public sealed class SamplingContext
{
    private const float Sqrt2 = 1.4142135623730951f;
    private const float Sqrt2Over2 = 0.7071067811865476f;
    private const int QuaternionBits = 15;
    private const int QuaternionScale = (1 << QuaternionBits) - 1;

    private readonly int _maxPaddedTracks;
    private readonly Cache _translations;
    private readonly Cache _rotations;
    private readonly Cache _scales;
    private readonly Vector3Keys[] _translationKeys;
    private readonly QuaternionKeys[] _rotationKeys;
    private readonly Vector3Keys[] _scaleKeys;
    private AnimationClip? _animation;
    private float _ratio;

    public SamplingContext(int maxTracks)
    {
        _maxPaddedTracks = AnimationClip.PaddedTrackCount(Math.Max(0, maxTracks));
        _translations = new Cache(_maxPaddedTracks);
        _rotations = new Cache(_maxPaddedTracks);
        _scales = new Cache(_maxPaddedTracks);
        _translationKeys = new Vector3Keys[_maxPaddedTracks];
        _rotationKeys = new QuaternionKeys[_maxPaddedTracks];
        _scaleKeys = new Vector3Keys[_maxPaddedTracks];
    }

    public int MaxTracks => _maxPaddedTracks;

    /// <summary>Forgets the cursor, so the next sample walks from an i-frame; needed only when the same context is reused for a different clip, which <see cref="Sample"/> detects itself.</summary>
    public void Invalidate()
    {
        _animation = null;
        _ratio = 0f;
        _translations.Next = 0;
        _rotations.Next = 0;
        _scales.Next = 0;
    }

    /// <summary>Samples at <paramref name="ratio"/> (clamped to 0..1) into <paramref name="output"/>, one pose per track; tracks past the output's length are skipped.</summary>
    /// <exception cref="ArgumentException">The clip has more tracks than this context was sized for.</exception>
    public void Sample(AnimationClip animation, float ratio, Span<JointPose> output)
    {
        ArgumentNullException.ThrowIfNull(animation);
        var padded = AnimationClip.PaddedTrackCount(animation.TrackCount);
        if (padded > _maxPaddedTracks) throw new ArgumentException($"The clip has {animation.TrackCount} tracks; this context samples at most {_maxPaddedTracks}.", nameof(animation));
        if (padded == 0) return;

        var clamped = Math.Clamp(ratio, 0f, 1f);
        if (!ReferenceEquals(_animation, animation))
        {
            Invalidate();
            _animation = animation;
        }

        var previousRatio = _ratio;
        _ratio = clamped;

        UpdateCache(clamped, previousRatio, padded, animation.Timepoints, animation.Translations, _translations);
        DecompressVector3(padded, animation.Timepoints, animation.Translations, _translations, _translationKeys);
        UpdateCache(clamped, previousRatio, padded, animation.Timepoints, animation.Rotations, _rotations);
        DecompressQuaternion(padded, animation.Timepoints, animation.Rotations, _rotations, _rotationKeys);
        UpdateCache(clamped, previousRatio, padded, animation.Timepoints, animation.Scales, _scales);
        DecompressVector3(padded, animation.Timepoints, animation.Scales, _scales, _scaleKeys);

        var count = Math.Min(output.Length, animation.TrackCount);
        for (var i = 0; i < count; i++)
        {
            ref readonly var t = ref _translationKeys[i];
            ref readonly var r = ref _rotationKeys[i];
            ref readonly var s = ref _scaleKeys[i];
            output[i] = new JointPose(
                Vector3.Lerp(t.Left, t.Right, Blend(clamped, t.LeftRatio, t.RightRatio)),
                NormalizedLerp(r.Left, r.Right, Blend(clamped, r.LeftRatio, r.RightRatio)),
                Vector3.Lerp(s.Left, s.Right, Blend(clamped, s.LeftRatio, s.RightRatio)));
        }
    }

    private static float Blend(float ratio, float left, float right) => (ratio - left) / (right - left);

    /// <summary>ozz interpolates rotations by normalized lerp; the builder already flipped each key onto the short arc from its predecessor.</summary>
    private static Quaternion NormalizedLerp(Quaternion a, Quaternion b, float t)
    {
        var lerp = new Quaternion(
            (b.X - a.X) * t + a.X,
            (b.Y - a.Y) * t + a.Y,
            (b.Z - a.Z) * t + a.Z,
            (b.W - a.W) * t + a.W);
        return Quaternion.Normalize(lerp);
    }

    private static void UpdateCache(float ratio, float previousRatio, int paddedTracks, float[] timepoints, KeyframeStream stream, Cache cache)
    {
        var numKeys = (uint)stream.KeyCount;
        var numTracks = (uint)paddedTracks;
        var next = cache.Next;
        var delta = ratio - previousRatio;

        if (next == 0 || Math.Abs(delta) > stream.IframeInterval / 2f)
        {
            var iframe = -1;
            if (stream.IframeDesc.Length > 0)
            {
                iframe = (int)(0.5f + ratio / stream.IframeInterval);
            }
            else if (next == 0 || delta < 0f)
            {
                iframe = 0;
            }

            if (iframe >= 0)
            {
                next = InitializeCache(stream, iframe, cache.Entries.AsSpan(0, paddedTracks));
                Array.Fill(cache.Outdated, true, 0, paddedTracks);
            }
        }

        var previouses = stream.Previouses;
        uint track = 0;
        for (; next < numKeys && KeyRatio(timepoints, stream, next - previouses[next]) <= ratio; next++)
        {
            track = TrackForward(cache.Entries, previouses, next, track, numTracks);
            cache.Outdated[track] = true;
            cache.Entries[track] = next;
        }

        for (; KeyRatio(timepoints, stream, (next - 1) - previouses[next - 1]) > ratio; next--)
        {
            track = TrackBackward(cache.Entries, next - 1, track, numTracks);
            cache.Outdated[track] = true;
            var previous = previouses[cache.Entries[track]];
            cache.Entries[track] -= previous;
        }

        cache.Next = next;
    }

    private static uint InitializeCache(KeyframeStream stream, int iframe, Span<uint> entries)
    {
        if (iframe > 0)
        {
            var at = (iframe - 1) * 2;
            if (at + 1 >= stream.IframeDesc.Length) throw new InvalidDataException($"The clip has no i-frame {iframe}.");
            GroupVarint.Decode(stream.IframeEntries, (int)stream.IframeDesc[at], entries);
            return stream.IframeDesc[at + 1] + 1;
        }

        var numTracks = (uint)entries.Length;
        for (uint i = 0; i < numTracks; i++) entries[(int)i] = i + numTracks;
        return numTracks * 2;
    }

    private static float KeyRatio(float[] timepoints, KeyframeStream stream, uint key) => timepoints[stream.TimepointOf((int)key)];

    private static uint TrackForward(uint[] entries, ushort[] previouses, uint key, uint lastTrack, uint numTracks)
    {
        var target = key - previouses[key];
        for (var entry = lastTrack; entry < numTracks; entry++)
        {
            if (entries[entry] == target) return entry;
        }

        for (uint entry = 0; entry < lastTrack; entry++)
        {
            if (entries[entry] == target) return entry;
        }

        throw new InvalidDataException($"The clip's key {key} links to key {target}, which no track's cursor holds.");
    }

    private static uint TrackBackward(uint[] entries, uint target, uint lastTrack, uint numTracks)
    {
        for (var entry = (int)lastTrack; entry >= 0; entry--)
        {
            if (entries[entry] == target) return (uint)entry;
        }

        for (var entry = (int)numTracks - 1; entry > lastTrack; entry--)
        {
            if (entries[entry] == target) return (uint)entry;
        }

        throw new InvalidDataException($"The clip's key {target} is not the cursor of any track.");
    }

    private static void DecompressVector3(int paddedTracks, float[] timepoints, KeyframeStream stream, Cache cache, Vector3Keys[] keys)
    {
        for (var i = 0; i < paddedTracks; i++)
        {
            if (!cache.Outdated[i]) continue;
            cache.Outdated[i] = false;
            var right = cache.Entries[i];
            var left = right - stream.Previouses[right];
            keys[i] = new Vector3Keys(
                KeyRatio(timepoints, stream, left), KeyRatio(timepoints, stream, right),
                ReadVector3(stream.Values, left), ReadVector3(stream.Values, right));
        }
    }

    private static void DecompressQuaternion(int paddedTracks, float[] timepoints, KeyframeStream stream, Cache cache, QuaternionKeys[] keys)
    {
        for (var i = 0; i < paddedTracks; i++)
        {
            if (!cache.Outdated[i]) continue;
            cache.Outdated[i] = false;
            var right = cache.Entries[i];
            var left = right - stream.Previouses[right];
            keys[i] = new QuaternionKeys(
                KeyRatio(timepoints, stream, left), KeyRatio(timepoints, stream, right),
                ReadQuaternion(stream.Values, left), ReadQuaternion(stream.Values, right));
        }
    }

    private static Vector3 ReadVector3(ushort[] values, uint key)
    {
        var at = (int)key * 3;
        return new Vector3(HalfFloat.ToSingle(values[at]), HalfFloat.ToSingle(values[at + 1]), HalfFloat.ToSingle(values[at + 2]));
    }

    /// <summary>Three 15-bit components in −√2/2..√2/2, the largest component omitted and rebuilt from the unit length with its sign bit.</summary>
    internal static Quaternion ReadQuaternion(ushort[] values, uint key)
    {
        var at = (int)key * 3;
        uint w0 = values[at], w1 = values[at + 1], w2 = values[at + 2];
        var packed = (w0 >> 3) | (w1 << 13) | (w2 << 29);
        var largest = (int)(w0 & 0x3);
        var negative = ((w0 >> 2) & 0x1) != 0;
        Span<float> component = stackalloc float[3];
        component[0] = (int)(packed & 0x7fff) * (Sqrt2 / QuaternionScale) - Sqrt2Over2;
        component[1] = (int)((packed >> 15) & 0x7fff) * (Sqrt2 / QuaternionScale) - Sqrt2Over2;
        component[2] = (int)(w2 >> 1) * (Sqrt2 / QuaternionScale) - Sqrt2Over2;

        Span<float> q = stackalloc float[4];
        var slot = 0;
        for (var c = 0; c < 4; c++)
        {
            if (c != largest) q[c] = component[slot++];
        }

        var remaining = 1f - (component[0] * component[0] + component[1] * component[1] + component[2] * component[2]);
        var restored = remaining > 0f ? MathF.Sqrt(remaining) : 0f;
        q[largest] = negative ? -restored : restored;
        return new Quaternion(q[0], q[1], q[2], q[3]);
    }

    private sealed class Cache(int paddedTracks)
    {
        public readonly uint[] Entries = new uint[paddedTracks];
        public readonly bool[] Outdated = new bool[paddedTracks];
        public uint Next;
    }

    private readonly record struct Vector3Keys(float LeftRatio, float RightRatio, Vector3 Left, Vector3 Right);

    private readonly record struct QuaternionKeys(float LeftRatio, float RightRatio, Quaternion Left, Quaternion Right);
}
