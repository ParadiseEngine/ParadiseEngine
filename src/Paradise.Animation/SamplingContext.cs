using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

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
/// A port of ozz-animation's <c>SamplingJob</c> (0.17), keeping its structure-of-arrays half:
/// keys are decoded and interpolated four tracks at a time in <see cref="Vector128{T}"/> lanes,
/// the cursor walk stays scalar. Where ozz uses estimated reciprocal and inverse square root
/// instructions, this uses exact division and square root; the poses differ from native ozz at
/// the fourth decimal and from the source clip by the quantization alone.
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
    private BlobArray<SoaVector3Keys> _translationKeys;
    private BlobArray<SoaQuaternionKeys> _rotationKeys;
    private BlobArray<SoaVector3Keys> _scaleKeys;

    public static NativeBlobAssetReference<SamplingContext> Create(int maxTracks)
    {
        var padded = AnimationBlob.PaddedTrackCount(Math.Max(0, maxTracks));
        var groups = padded / 4;
        var builder = new StructBuilder<SamplingContext>();
        builder.Value.MaxPaddedTracks = padded;
        Cache.Set(builder, ref builder.Value._translations, padded);
        Cache.Set(builder, ref builder.Value._rotations, padded);
        Cache.Set(builder, ref builder.Value._scales, padded);
        builder.SetArray(ref builder.Value._translationKeys, new SoaVector3Keys[groups], alignment: 16);
        builder.SetArray(ref builder.Value._rotationKeys, new SoaQuaternionKeys[groups], alignment: 16);
        builder.SetArray(ref builder.Value._scaleKeys, new SoaVector3Keys[groups], alignment: 16);
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

    /// <summary>Samples at <paramref name="ratio"/> (clamped to 0..1) into <paramref name="output"/>, one lane per track; the output must be sized for at least the clip's tracks.</summary>
    /// <exception cref="ArgumentException">The clip has more tracks than this context or the output was sized for.</exception>
    public unsafe void Sample(ref AnimationBlob clip, float ratio, ref JointPoses output)
    {
        var padded = clip.PaddedTracks;
        if (padded > MaxPaddedTracks) throw new ArgumentException($"The clip has {clip.TrackCount} tracks; this context samples at most {MaxPaddedTracks}.", nameof(clip));
        if (output.JointCount < clip.TrackCount) throw new ArgumentException($"The clip has {clip.TrackCount} tracks; the output holds {output.JointCount} joints.", nameof(output));
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
        var groups = padded / 4;
        var timepoints = clip.Timepoints.ToSpan();
        var translations = new StreamView(ref clip.Translations, timepoints.Length);
        var rotations = new StreamView(ref clip.Rotations, timepoints.Length);
        var scales = new StreamView(ref clip.Scales, timepoints.Length);
        var translationKeys = _translationKeys.ToSpan()[..groups];
        var rotationKeys = _rotationKeys.ToSpan()[..groups];
        var scaleKeys = _scaleKeys.ToSpan()[..groups];

        UpdateCache(clamped, previousRatio, padded, timepoints, in translations, ref _translations);
        DecompressVector3(groups, timepoints, in translations, ref _translations, translationKeys);
        UpdateCache(clamped, previousRatio, padded, timepoints, in rotations, ref _rotations);
        DecompressQuaternion(groups, timepoints, in rotations, ref _rotations, rotationKeys);
        UpdateCache(clamped, previousRatio, padded, timepoints, in scales, ref _scales);
        DecompressVector3(groups, timepoints, in scales, ref _scales, scaleKeys);

        Interpolate(clamped, groups, translationKeys, rotationKeys, scaleKeys, ref output);
    }

    /// <summary>Four tracks per lane: blend factors, lerp for translation and scale, normalized lerp for rotation, written straight into the output's groups — the same layout, no transpose.</summary>
    private static void Interpolate(float ratio, int groups, ReadOnlySpan<SoaVector3Keys> translations, ReadOnlySpan<SoaQuaternionKeys> rotations, ReadOnlySpan<SoaVector3Keys> scales, ref JointPoses output)
    {
        var at = Vector128.Create(ratio);
        var outT = output.Translations.ToSpan();
        var outR = output.Rotations.ToSpan();
        var outS = output.Scales.ToSpan();
        for (var g = 0; g < groups; g++)
        {
            ref readonly var t = ref translations[g];
            ref readonly var r = ref rotations[g];
            ref readonly var s = ref scales[g];

            var tBlend = (at - t.LeftRatio) / (t.RightRatio - t.LeftRatio);
            ref var ot = ref outT[g];
            ot.X = (t.RightX - t.LeftX) * tBlend + t.LeftX;
            ot.Y = (t.RightY - t.LeftY) * tBlend + t.LeftY;
            ot.Z = (t.RightZ - t.LeftZ) * tBlend + t.LeftZ;

            var rBlend = (at - r.LeftRatio) / (r.RightRatio - r.LeftRatio);
            var rx = (r.RightX - r.LeftX) * rBlend + r.LeftX;
            var ry = (r.RightY - r.LeftY) * rBlend + r.LeftY;
            var rz = (r.RightZ - r.LeftZ) * rBlend + r.LeftZ;
            var rw = (r.RightW - r.LeftW) * rBlend + r.LeftW;
            var inverseLength = Vector128<float>.One / Vector128.Sqrt(rx * rx + ry * ry + rz * rz + rw * rw);
            ref var or = ref outR[g];
            or.X = rx * inverseLength;
            or.Y = ry * inverseLength;
            or.Z = rz * inverseLength;
            or.W = rw * inverseLength;

            var sBlend = (at - s.LeftRatio) / (s.RightRatio - s.LeftRatio);
            ref var os = ref outS[g];
            os.X = (s.RightX - s.LeftX) * sBlend + s.LeftX;
            os.Y = (s.RightY - s.LeftY) * sBlend + s.LeftY;
            os.Z = (s.RightZ - s.LeftZ) * sBlend + s.LeftZ;
        }
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

        var track = 0;
        for (; next < numKeys && stream.KeyRatio(timepoints, next - previouses[(int)next]) <= ratio; next++)
        {
            track = TrackForward(entries, next - previouses[(int)next], track);
            outdated[track] = 1;
            entries[track] = next;
        }

        for (; stream.KeyRatio(timepoints, (next - 1) - previouses[(int)next - 1]) > ratio; next--)
        {
            track = TrackBackward(entries, next - 1, track);
            outdated[track] = 1;
            entries[track] -= previouses[(int)entries[track]];
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

    /// <summary>The track whose cursor holds <paramref name="target"/>, scanned from the last hit onward then from the start. Keys arrive roughly round-robin, so the hit is usually the next entry or two: a plain loop beats a vectorized <c>IndexOf</c> here, whose setup costs more than the few compares it saves.</summary>
    private static int TrackForward(ReadOnlySpan<uint> entries, uint target, int lastTrack)
    {
        for (var entry = lastTrack; entry < entries.Length; entry++)
        {
            if (entries[entry] == target) return entry;
        }

        for (var entry = 0; entry < lastTrack; entry++)
        {
            if (entries[entry] == target) return entry;
        }

        throw new InvalidDataException($"The clip links a key to key {target}, which no track's cursor holds.");
    }

    private static int TrackBackward(ReadOnlySpan<uint> entries, uint target, int lastTrack)
    {
        for (var entry = lastTrack; entry >= 0; entry--)
        {
            if (entries[entry] == target) return entry;
        }

        for (var entry = entries.Length - 1; entry > lastTrack; entry--)
        {
            if (entries[entry] == target) return entry;
        }

        throw new InvalidDataException($"The clip's key {target} is not the cursor of any track.");
    }

    /// <summary>Re-decodes each group of four tracks in which any cursor moved: both keys' ratios and half-float values, one lane per track.</summary>
    private static void DecompressVector3(int groups, ReadOnlySpan<float> timepoints, in StreamView stream, ref Cache cache, Span<SoaVector3Keys> keys)
    {
        var entries = cache.Entries.ToSpan();
        var outdated = cache.Outdated.ToSpan();
        var values = stream.Values;
        for (var g = 0; g < groups; g++)
        {
            if (Unsafe.ReadUnaligned<uint>(ref outdated[g * 4]) == 0) continue;
            Unsafe.WriteUnaligned(ref outdated[g * 4], 0u);

            var r0 = entries[g * 4]; var r1 = entries[g * 4 + 1]; var r2 = entries[g * 4 + 2]; var r3 = entries[g * 4 + 3];
            var l0 = r0 - stream.Previouses[(int)r0]; var l1 = r1 - stream.Previouses[(int)r1]; var l2 = r2 - stream.Previouses[(int)r2]; var l3 = r3 - stream.Previouses[(int)r3];
            ref var key = ref keys[g];
            key.LeftRatio = Vector128.Create(stream.KeyRatio(timepoints, l0), stream.KeyRatio(timepoints, l1), stream.KeyRatio(timepoints, l2), stream.KeyRatio(timepoints, l3));
            key.RightRatio = Vector128.Create(stream.KeyRatio(timepoints, r0), stream.KeyRatio(timepoints, r1), stream.KeyRatio(timepoints, r2), stream.KeyRatio(timepoints, r3));
            int a = (int)l0 * 3, b = (int)l1 * 3, c = (int)l2 * 3, d = (int)l3 * 3;
            key.LeftX = HalfToSingle(Vector128.Create((int)values[a], values[b], values[c], values[d]));
            key.LeftY = HalfToSingle(Vector128.Create((int)values[a + 1], values[b + 1], values[c + 1], values[d + 1]));
            key.LeftZ = HalfToSingle(Vector128.Create((int)values[a + 2], values[b + 2], values[c + 2], values[d + 2]));
            a = (int)r0 * 3; b = (int)r1 * 3; c = (int)r2 * 3; d = (int)r3 * 3;
            key.RightX = HalfToSingle(Vector128.Create((int)values[a], values[b], values[c], values[d]));
            key.RightY = HalfToSingle(Vector128.Create((int)values[a + 1], values[b + 1], values[c + 1], values[d + 1]));
            key.RightZ = HalfToSingle(Vector128.Create((int)values[a + 2], values[b + 2], values[c + 2], values[d + 2]));
        }
    }

    private static void DecompressQuaternion(int groups, ReadOnlySpan<float> timepoints, in StreamView stream, ref Cache cache, Span<SoaQuaternionKeys> keys)
    {
        var entries = cache.Entries.ToSpan();
        var outdated = cache.Outdated.ToSpan();
        for (var g = 0; g < groups; g++)
        {
            if (Unsafe.ReadUnaligned<uint>(ref outdated[g * 4]) == 0) continue;
            Unsafe.WriteUnaligned(ref outdated[g * 4], 0u);

            var r0 = entries[g * 4]; var r1 = entries[g * 4 + 1]; var r2 = entries[g * 4 + 2]; var r3 = entries[g * 4 + 3];
            var l0 = r0 - stream.Previouses[(int)r0]; var l1 = r1 - stream.Previouses[(int)r1]; var l2 = r2 - stream.Previouses[(int)r2]; var l3 = r3 - stream.Previouses[(int)r3];
            ref var key = ref keys[g];
            key.LeftRatio = Vector128.Create(stream.KeyRatio(timepoints, l0), stream.KeyRatio(timepoints, l1), stream.KeyRatio(timepoints, l2), stream.KeyRatio(timepoints, l3));
            key.RightRatio = Vector128.Create(stream.KeyRatio(timepoints, r0), stream.KeyRatio(timepoints, r1), stream.KeyRatio(timepoints, r2), stream.KeyRatio(timepoints, r3));
            ReadQuaternions(stream.Values, l0, l1, l2, l3, out key.LeftX, out key.LeftY, out key.LeftZ, out key.LeftW);
            ReadQuaternions(stream.Values, r0, r1, r2, r3, out key.RightX, out key.RightY, out key.RightZ, out key.RightW);
        }
    }

    /// <summary>ozz's half-to-float in integer lanes (<c>simd_math_ref-inl.h</c>): shift the exponent and mantissa into place, rescale by a power of two, keep infinities.</summary>
    private static Vector128<float> HalfToSingle(Vector128<int> halves)
    {
        var magic = Vector128.Create((254 - 15) << 23).AsSingle();
        var infinity = Vector128.Create((127 + 16) << 23).AsSingle();
        var sign = halves & Vector128.Create(0x8000);
        var exponentMantissa = (halves & Vector128.Create(0x7fff)) << 13;
        var adjusted = exponentMantissa.AsSingle() * magic;
        var isInfinite = Vector128.GreaterThanOrEqual(adjusted, infinity).AsInt32();
        var bits = Vector128.ConditionalSelect(isInfinite, adjusted.AsInt32() | Vector128.Create(255 << 23), adjusted.AsInt32());
        return (bits | (sign << 16)).AsSingle();
    }

    /// <summary>Four packed keys to lanes: three 15-bit components in −√2/2..√2/2 each, the largest component omitted and rebuilt from the unit length with its sign bit.</summary>
    internal static void ReadQuaternions(ReadOnlySpan<ushort> values, uint k0, uint k1, uint k2, uint k3, out Vector128<float> x, out Vector128<float> y, out Vector128<float> z, out Vector128<float> w)
    {
        Unpack(values, k0, out var largest0, out var sign0, out var a0, out var b0, out var c0);
        Unpack(values, k1, out var largest1, out var sign1, out var a1, out var b1, out var c1);
        Unpack(values, k2, out var largest2, out var sign2, out var a2, out var b2, out var c2);
        Unpack(values, k3, out var largest3, out var sign3, out var a3, out var b3, out var c3);

        // Component c of key k is stored component Mapping[largest][c]; the largest component
        // itself reads a placeholder and is overwritten below.
        var scale = Vector128.Create(Sqrt2 / QuaternionScale);
        var offset = Vector128.Create(-Sqrt2Over2);
        var component0 = Vector128.Create(a0, a1, a2, a3);
        var component1 = Vector128.Create(b0, b1, b2, b3);
        var component2 = Vector128.Create(c0, c1, c2, c3);
        var largest = Vector128.Create(largest0, largest1, largest2, largest3);
        var isLargest0 = Vector128.Equals(largest, Vector128.Create(0));
        var isLargest1 = Vector128.Equals(largest, Vector128.Create(1));
        var isLargest2 = Vector128.Equals(largest, Vector128.Create(2));
        var isLargest3 = Vector128.Equals(largest, Vector128.Create(3));

        // x takes stored 0 unless x is the largest; y takes stored 0 when x was largest, else 1; and so on.
        var xi = Vector128.ConditionalSelect(isLargest0, Vector128<int>.Zero, component0);
        var yi = Vector128.ConditionalSelect(isLargest0, component0, Vector128.ConditionalSelect(isLargest1, Vector128<int>.Zero, component1));
        var zi = Vector128.ConditionalSelect(isLargest0 | isLargest1, component1, Vector128.ConditionalSelect(isLargest2, Vector128<int>.Zero, component2));
        var wi = Vector128.ConditionalSelect(isLargest3, Vector128<int>.Zero, component2);

        var xf = Vector128.ConvertToSingle(xi) * scale + offset;
        var yf = Vector128.ConvertToSingle(yi) * scale + offset;
        var zf = Vector128.ConvertToSingle(zi) * scale + offset;
        var wf = Vector128.ConvertToSingle(wi) * scale + offset;
        xf = Vector128.ConditionalSelect(isLargest0.AsSingle(), Vector128<float>.Zero, xf);
        yf = Vector128.ConditionalSelect(isLargest1.AsSingle(), Vector128<float>.Zero, yf);
        zf = Vector128.ConditionalSelect(isLargest2.AsSingle(), Vector128<float>.Zero, zf);
        wf = Vector128.ConditionalSelect(isLargest3.AsSingle(), Vector128<float>.Zero, wf);

        var remaining = Vector128.Create(1f) - (xf * xf + yf * yf + zf * zf + wf * wf);
        var restored = Vector128.Sqrt(Vector128.Max(remaining, Vector128<float>.Zero));
        var negative = Vector128.Create(sign0, sign1, sign2, sign3) << 31;
        restored = (restored.AsInt32() | negative).AsSingle();
        x = Vector128.ConditionalSelect(isLargest0.AsSingle(), restored, xf);
        y = Vector128.ConditionalSelect(isLargest1.AsSingle(), restored, yf);
        z = Vector128.ConditionalSelect(isLargest2.AsSingle(), restored, zf);
        w = Vector128.ConditionalSelect(isLargest3.AsSingle(), restored, wf);
    }

    private static void Unpack(ReadOnlySpan<ushort> values, uint key, out int largest, out int sign, out int a, out int b, out int c)
    {
        var at = (int)key * 3;
        uint w0 = values[at], w1 = values[at + 1], w2 = values[at + 2];
        var packed = (w0 >> 3) | (w1 << 13) | (w2 << 29);
        largest = (int)(w0 & 0x3);
        sign = (int)((w0 >> 2) & 0x1);
        a = (int)(packed & 0x7fff);
        b = (int)((packed >> 15) & 0x7fff);
        c = (int)(w2 >> 1);
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

    /// <summary>Four tracks' left and right keys, one lane each.</summary>
    private struct SoaVector3Keys
    {
        public Vector128<float> LeftRatio, RightRatio;
        public Vector128<float> LeftX, LeftY, LeftZ;
        public Vector128<float> RightX, RightY, RightZ;
    }

    private struct SoaQuaternionKeys
    {
        public Vector128<float> LeftRatio, RightRatio;
        public Vector128<float> LeftX, LeftY, LeftZ, LeftW;
        public Vector128<float> RightX, RightY, RightZ, RightW;
    }
}
