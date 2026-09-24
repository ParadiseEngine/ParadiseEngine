using System.Text;

using Paradise.BLOB;

namespace Paradise.Animation;

/// <summary>Stores an ozz-animation clip in one native blob.</summary>
/// <remarks>Translation, rotation and scale streams merge tracks by time with per-track back-links.
/// Translation and scale use half floats; rotations use three 15-bit components.
/// Streams pad tracks to multiples of four for SIMD sampling; OzzArchive.ReadAnimation loads archives.</remarks>
public struct AnimationBlob
{
    public float Duration;
    public int TrackCount;
    public BlobString<UTF8Encoding> Name;

    /// <summary>Every distinct key time as a ratio of <see cref="Duration"/>, ascending; keys index into it.</summary>
    public BlobArray<float> Timepoints;

    public KeyframeStreamBlob Translations;
    public KeyframeStreamBlob Rotations;
    public KeyframeStreamBlob Scales;

    /// <summary>ozz samples four tracks at once, so every stream carries identity keys up to the next multiple of four.</summary>
    public static int PaddedTrackCount(int trackCount) => (trackCount + 3) & ~3;

    public int PaddedTracks => PaddedTrackCount(TrackCount);

    /// <exception cref="ArgumentException">A stream that does not fit the track count or timepoints.</exception>
    internal static NativeBlobAssetReference<AnimationBlob> Create(string name, float duration, int trackCount, float[] timepoints, KeyframeStreamData translations, KeyframeStreamData rotations, KeyframeStreamData scales)
    {
        if (duration <= 0f) throw new ArgumentException("A clip's duration is positive.", nameof(duration));
        if (trackCount < 0 || trackCount > SkeletonBlob.MaxJoints) throw new ArgumentException($"{trackCount} tracks is outside 0..{SkeletonBlob.MaxJoints}.", nameof(trackCount));
        if (timepoints.Length > ushort.MaxValue) throw new ArgumentException("A clip holds at most 65535 distinct key times.", nameof(timepoints));
        var padded = PaddedTrackCount(trackCount);
        translations.Check("translation", padded, timepoints);
        rotations.Check("rotation", padded, timepoints);
        scales.Check("scale", padded, timepoints);

        var builder = new StructBuilder<AnimationBlob>();
        builder.Value.Duration = duration;
        builder.Value.TrackCount = trackCount;
        builder.SetString(ref builder.Value.Name, name);
        builder.SetArray(ref builder.Value.Timepoints, timepoints);
        translations.Set(builder, ref builder.Value.Translations);
        rotations.Set(builder, ref builder.Value.Rotations);
        scales.Set(builder, ref builder.Value.Scales);
        return builder.CreateNativeBlobAssetReference();
    }
}

/// <summary>Stores one component's time-ordered keys across all tracks.</summary>
/// <remarks>Ratios index timepoints using one byte for at most 256 times, otherwise two.
/// Previouses stores per-track back-links; Values holds three 16-bit words per key.
/// Group-varint i-frames snapshot per-track cursors for seeking.</remarks>
public struct KeyframeStreamBlob
{
    public BlobArray<byte> Ratios;
    public BlobArray<ushort> Previouses;
    public BlobArray<ushort> Values;
    public BlobArray<byte> IframeEntries;

    /// <summary>Pairs of (byte offset into <see cref="IframeEntries"/>, index of the last key the snapshot covers).</summary>
    public BlobArray<uint> IframeDesc;

    /// <summary>Spacing of the i-frames as a ratio of the clip; 1 when there are none.</summary>
    public float IframeInterval;

    public int KeyCount => Previouses.Length;

    public int RatioBytes => KeyCount == 0 ? 1 : Ratios.Length / KeyCount;

    /// <summary>The timepoint index of key <paramref name="key"/>.</summary>
    public int TimepointOf(int key) => RatioBytes == 1 ? Ratios[key] : Ratios[key * 2] | (Ratios[key * 2 + 1] << 8);
}

/// <summary>The managed staging of a stream, what the archive reader and the builder hand to <see cref="AnimationBlob.Create"/>.</summary>
internal sealed record KeyframeStreamData(byte[] Ratios, ushort[] Previouses, ushort[] Values, byte[] IframeEntries, uint[] IframeDesc, float IframeInterval)
{
    public int KeyCount => Previouses.Length;

    public int TimepointOf(int key, int ratioBytes) => ratioBytes == 1 ? Ratios[key] : Ratios[key * 2] | (Ratios[key * 2 + 1] << 8);

    /// <summary>Everything the sampler indexes with or divides by, so a bad archive fails here by name and never inside <see cref="SamplingContext.Sample"/>.</summary>
    public void Check(string component, int paddedTracks, float[] timepoints)
    {
        var timepointCount = timepoints.Length;
        var ratioBytes = timepointCount <= byte.MaxValue ? 1 : 2;
        if (Ratios.Length != KeyCount * ratioBytes) throw new ArgumentException($"The {component} stream has {KeyCount} keys and {Ratios.Length} ratio bytes.");
        if (Values.Length != KeyCount * 3) throw new ArgumentException($"The {component} stream has {KeyCount} keys and {Values.Length} value words.");
        if (paddedTracks > 0 && KeyCount < paddedTracks * 2) throw new ArgumentException($"The {component} stream has {KeyCount} keys; every one of {paddedTracks} tracks needs a first and a last key.");
        if (IframeDesc.Length % 2 != 0) throw new ArgumentException($"The {component} stream's i-frame table has an odd length.");
        if (IframeDesc.Length > 0 && !(IframeInterval > 0f)) throw new ArgumentException($"The {component} stream has i-frames at an interval of {IframeInterval}; it must be positive.");
        for (var i = 0; i < IframeDesc.Length; i += 2)
        {
            if (IframeDesc[i] >= (uint)IframeEntries.Length) throw new ArgumentException($"The {component} stream's i-frame {i / 2} starts at byte {IframeDesc[i]} of {IframeEntries.Length}.");
            if (IframeDesc[i + 1] >= (uint)KeyCount) throw new ArgumentException($"The {component} stream's i-frame {i / 2} covers key {IframeDesc[i + 1]} of {KeyCount}.");
        }

        for (var i = 0; i < KeyCount; i++)
        {
            if (TimepointOf(i, ratioBytes) >= timepointCount) throw new ArgumentException($"The {component} key {i} names timepoint {TimepointOf(i, ratioBytes)} of {timepointCount}.");
            if (Previouses[i] > i) throw new ArgumentException($"The {component} key {i} links {Previouses[i]} keys back, before the stream's start.");
        }

        // The backward walk stops at the first key whose ratio is not past the target; a first key
        // after the clip's start would walk it off the front of the stream.
        if (KeyCount > 0 && timepoints[TimepointOf(0, ratioBytes)] != 0f) throw new ArgumentException($"The {component} stream's first key sits at ratio {timepoints[TimepointOf(0, ratioBytes)]}, not at the clip's start.");
    }

    public void Set(StructBuilder<AnimationBlob> builder, ref KeyframeStreamBlob stream)
    {
        builder.SetArray(ref stream.Ratios, Ratios);
        builder.SetArray(ref stream.Previouses, Previouses);
        builder.SetArray(ref stream.Values, Values);
        builder.SetArray(ref stream.IframeEntries, IframeEntries);
        builder.SetArray(ref stream.IframeDesc, IframeDesc);
        stream.IframeInterval = IframeInterval;
    }
}
