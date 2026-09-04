using System.Numerics;
using System.Runtime.CompilerServices;

using Paradise.BLOB;

namespace Paradise.Animation;

/// <summary>
/// Samples an <see cref="AnimationBlob"/> at a ratio of its duration into one local pose per
/// track. A native blob holding the per-track cursor ozz's sampler keeps between calls: stepping
/// forward in time from the last sample visits only the keys that passed, and a seek restarts
/// from the nearest i-frame. Create one per playing instance with <see cref="Create"/> and keep
/// using it for that instance; sampling allocates nothing.
/// </summary>
/// <remarks>
/// A scalar port of ozz-animation's <c>SamplingJob</c> (0.17). Where ozz uses estimated reciprocal
/// and inverse square root instructions, this uses exact division and square root; the poses
/// differ from native ozz at the fourth decimal and from the source clip by the quantization alone.
/// </remarks>
public struct SamplingContext
{
    private const float Sqrt2 = 1.4142135623730951f;
    private const float Sqrt2Over2 = 0.7071067811865476f;
    private const int QuaternionBits = 15;
    private const int QuaternionScale = (1 << QuaternionBits) - 1;

    public int MaxPaddedTracks;
    private float _ratio;
    private nint _clip;
    private Cache _translations;
    private Cache _rotations;
    private Cache _scales;
    private BlobArray<Vector3Keys> _translationKeys;
    private BlobArray<QuaternionKeys> _rotationKeys;
    private BlobArray<Vector3Keys> _scaleKeys;

    public static NativeBlobAssetReference<SamplingContext> Create(int maxTracks)
    {
        var padded = AnimationBlob.PaddedTrackCount(Math.Max(0, maxTracks));
        var builder = new StructBuilder<SamplingContext>();
        builder.Value.MaxPaddedTracks = padded;
        Cache.Set(builder, ref builder.Value._translations, padded);
        Cache.Set(builder, ref builder.Value._rotations, padded);
        Cache.Set(builder, ref builder.Value._scales, padded);
        builder.SetArray(ref builder.Value._translationKeys, new Vector3Keys[padded]);
        builder.SetArray(ref builder.Value._rotationKeys, new QuaternionKeys[padded]);
        builder.SetArray(ref builder.Value._scaleKeys, new Vector3Keys[padded]);
        return builder.CreateNativeBlobAssetReference();
    }

    /// <summary>Forgets the cursor, so the next sample walks from an i-frame; <see cref="Sample"/> does this itself when handed a different clip.</summary>
    public void Invalidate()
    {
        _clip = 0;
        _ratio = 0f;
        _translations.Next = 0;
        _rotations.Next = 0;
        _scales.Next = 0;
    }

    /// <summary>Samples at <paramref name="ratio"/> (clamped to 0..1) into <paramref name="output"/>, one pose per track; tracks past the output's length are skipped.</summary>
    /// <exception cref="ArgumentException">The clip has more tracks than this context was sized for.</exception>
    public unsafe void Sample(ref AnimationBlob clip, float ratio, Span<JointPose> output)
    {
        var padded = clip.PaddedTracks;
        if (padded > MaxPaddedTracks) throw new ArgumentException($"The clip has {clip.TrackCount} tracks; this context samples at most {MaxPaddedTracks}.", nameof(clip));
        if (padded == 0) return;

        var clamped = Math.Clamp(ratio, 0f, 1f);
        var identity = (nint)Unsafe.AsPointer(ref clip);
        if (_clip != identity)
        {
            Invalidate();
            _clip = identity;
        }

        var previousRatio = _ratio;
        _ratio = clamped;

        // Spans once per call: a BlobArray indexer re-derives its pointer from the relative offset
        // and bounds-checks on every access, which doubled the cost of the loops below.
        var timepoints = clip.Timepoints.ToSpan();
        var translations = new StreamView(ref clip.Translations, timepoints.Length);
        var rotations = new StreamView(ref clip.Rotations, timepoints.Length);
        var scales = new StreamView(ref clip.Scales, timepoints.Length);
        var translationKeys = _translationKeys.ToSpan()[..padded];
        var rotationKeys = _rotationKeys.ToSpan()[..padded];
        var scaleKeys = _scaleKeys.ToSpan()[..padded];

        UpdateCache(clamped, previousRatio, padded, timepoints, in translations, ref _translations);
        DecompressVector3(padded, timepoints, in translations, ref _translations, translationKeys);
        UpdateCache(clamped, previousRatio, padded, timepoints, in rotations, ref _rotations);
        DecompressQuaternion(padded, timepoints, in rotations, ref _rotations, rotationKeys);
        UpdateCache(clamped, previousRatio, padded, timepoints, in scales, ref _scales);
        DecompressVector3(padded, timepoints, in scales, ref _scales, scaleKeys);

        var count = Math.Min(output.Length, clip.TrackCount);
        for (var i = 0; i < count; i++)
        {
            ref var t = ref translationKeys[i];
            ref var r = ref rotationKeys[i];
            ref var s = ref scaleKeys[i];
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

    /// <summary>One stream's arrays as spans, taken once per sample.</summary>
    private readonly ref struct StreamView
    {
        public readonly Span<byte> Ratios;
        public readonly Span<ushort> Previouses;
        public readonly Span<ushort> Values;
        public readonly Span<byte> IframeEntries;
        public readonly Span<uint> IframeDesc;
        public readonly float IframeInterval;
        public readonly bool WideRatios;

        public StreamView(ref KeyframeStreamBlob stream, int timepointCount)
        {
            Ratios = stream.Ratios.ToSpan();
            Previouses = stream.Previouses.ToSpan();
            Values = stream.Values.ToSpan();
            IframeEntries = stream.IframeEntries.ToSpan();
            IframeDesc = stream.IframeDesc.ToSpan();
            IframeInterval = stream.IframeInterval;
            WideRatios = timepointCount > byte.MaxValue;
        }

        public int KeyCount => Previouses.Length;

        public float KeyRatio(ReadOnlySpan<float> timepoints, uint key)
        {
            var at = (int)key;
            return timepoints[WideRatios ? Ratios[at * 2] | (Ratios[at * 2 + 1] << 8) : Ratios[at]];
        }
    }

    private static void UpdateCache(float ratio, float previousRatio, int paddedTracks, ReadOnlySpan<float> timepoints, in StreamView stream, ref Cache cache)
    {
        var numKeys = (uint)stream.KeyCount;
        var numTracks = (uint)paddedTracks;
        var entries = cache.Entries.ToSpan()[..paddedTracks];
        var outdated = cache.Outdated.ToSpan()[..paddedTracks];
        var previouses = stream.Previouses;
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
                next = InitializeCache(in stream, iframe, entries);
                outdated.Fill(1);
            }
        }

        uint track = 0;
        for (; next < numKeys && stream.KeyRatio(timepoints, next - previouses[(int)next]) <= ratio; next++)
        {
            track = TrackForward(entries, previouses, next, track, numTracks);
            outdated[(int)track] = 1;
            entries[(int)track] = next;
        }

        for (; stream.KeyRatio(timepoints, (next - 1) - previouses[(int)next - 1]) > ratio; next--)
        {
            track = TrackBackward(entries, next - 1, track, numTracks);
            outdated[(int)track] = 1;
            var previous = previouses[(int)entries[(int)track]];
            entries[(int)track] -= previous;
        }

        cache.Next = next;
    }

    private static uint InitializeCache(in StreamView stream, int iframe, Span<uint> entries)
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

    private static uint TrackForward(ReadOnlySpan<uint> entries, ReadOnlySpan<ushort> previouses, uint key, uint lastTrack, uint numTracks)
    {
        var target = key - previouses[(int)key];
        for (var entry = lastTrack; entry < numTracks; entry++)
        {
            if (entries[(int)entry] == target) return entry;
        }

        for (uint entry = 0; entry < lastTrack; entry++)
        {
            if (entries[(int)entry] == target) return entry;
        }

        throw new InvalidDataException($"The clip's key {key} links to key {target}, which no track's cursor holds.");
    }

    private static uint TrackBackward(ReadOnlySpan<uint> entries, uint target, uint lastTrack, uint numTracks)
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

    private static void DecompressVector3(int paddedTracks, ReadOnlySpan<float> timepoints, in StreamView stream, ref Cache cache, Span<Vector3Keys> keys)
    {
        var entries = cache.Entries.ToSpan();
        var outdated = cache.Outdated.ToSpan();
        for (var i = 0; i < paddedTracks; i++)
        {
            if (outdated[i] == 0) continue;
            outdated[i] = 0;
            var right = entries[i];
            var left = right - stream.Previouses[(int)right];
            keys[i] = new Vector3Keys(
                stream.KeyRatio(timepoints, left), stream.KeyRatio(timepoints, right),
                ReadVector3(stream.Values, left), ReadVector3(stream.Values, right));
        }
    }

    private static void DecompressQuaternion(int paddedTracks, ReadOnlySpan<float> timepoints, in StreamView stream, ref Cache cache, Span<QuaternionKeys> keys)
    {
        var entries = cache.Entries.ToSpan();
        var outdated = cache.Outdated.ToSpan();
        for (var i = 0; i < paddedTracks; i++)
        {
            if (outdated[i] == 0) continue;
            outdated[i] = 0;
            var right = entries[i];
            var left = right - stream.Previouses[(int)right];
            keys[i] = new QuaternionKeys(
                stream.KeyRatio(timepoints, left), stream.KeyRatio(timepoints, right),
                ReadQuaternion(stream.Values, left), ReadQuaternion(stream.Values, right));
        }
    }

    private static Vector3 ReadVector3(ReadOnlySpan<ushort> values, uint key)
    {
        var at = (int)key * 3;
        return new Vector3(HalfFloat.ToSingle(values[at]), HalfFloat.ToSingle(values[at + 1]), HalfFloat.ToSingle(values[at + 2]));
    }

    /// <summary>Three 15-bit components in −√2/2..√2/2, the largest component omitted and rebuilt from the unit length with its sign bit.</summary>
    internal static Quaternion ReadQuaternion(ReadOnlySpan<ushort> values, uint key)
    {
        var at = (int)key * 3;
        uint w0 = values[at], w1 = values[at + 1], w2 = values[at + 2];
        var packed = (w0 >> 3) | (w1 << 13) | (w2 << 29);
        var largest = (int)(w0 & 0x3);
        var negative = ((w0 >> 2) & 0x1) != 0;
        var c0 = (int)(packed & 0x7fff) * (Sqrt2 / QuaternionScale) - Sqrt2Over2;
        var c1 = (int)((packed >> 15) & 0x7fff) * (Sqrt2 / QuaternionScale) - Sqrt2Over2;
        var c2 = (int)(w2 >> 1) * (Sqrt2 / QuaternionScale) - Sqrt2Over2;
        var remaining = 1f - (c0 * c0 + c1 * c1 + c2 * c2);
        var restored = remaining > 0f ? MathF.Sqrt(remaining) : 0f;
        if (negative) restored = -restored;
        return largest switch
        {
            0 => new Quaternion(restored, c0, c1, c2),
            1 => new Quaternion(c0, restored, c1, c2),
            2 => new Quaternion(c0, c1, restored, c2),
            _ => new Quaternion(c0, c1, c2, restored),
        };
    }

    private struct Cache
    {
        public BlobArray<uint> Entries;
        public BlobArray<byte> Outdated;
        public uint Next;

        public static void Set(StructBuilder<SamplingContext> builder, ref Cache cache, int paddedTracks)
        {
            builder.SetArray(ref cache.Entries, new uint[paddedTracks]);
            builder.SetArray(ref cache.Outdated, new byte[paddedTracks]);
        }
    }

    private readonly record struct Vector3Keys(float LeftRatio, float RightRatio, Vector3 Left, Vector3 Right);

    private readonly record struct QuaternionKeys(float LeftRatio, float RightRatio, Quaternion Left, Quaternion Right);
}
