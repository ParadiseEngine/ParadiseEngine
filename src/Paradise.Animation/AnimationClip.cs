using System.Text;

namespace Paradise.Animation;

/// <summary>
/// An ozz-animation runtime clip: three keyframe streams (translation, rotation, scale), each sorted
/// by time across all tracks with a back-link from every key to the previous key of its track, so
/// a sampler advancing in time touches only the keys that change. Values are quantized: half
/// floats for translation and scale, three 15-bit components for rotation.
/// </summary>
/// <remarks>
/// Reads and writes the <c>ozz-animation</c> archive, version 7 (ozz-animation 0.17). The layout is
/// <c>Paradise.Animation.Offline.AnimationBuilder</c>'s output and ozz's <c>AnimationBuilder</c>'s,
/// byte for byte; <see cref="SamplingContext"/> is the reader. Track count is padded to a multiple
/// of four in every stream, as ozz's SIMD sampler requires, so a file cooked here plays in ozz too.
/// </remarks>
public sealed class AnimationClip
{
    public const string Tag = "ozz-animation";

    public const uint Version = 7;

    public AnimationClip(string name, float duration, int trackCount, float[] timepoints, KeyframeStream translations, KeyframeStream rotations, KeyframeStream scales)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(timepoints);
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentNullException.ThrowIfNull(rotations);
        ArgumentNullException.ThrowIfNull(scales);
        if (duration <= 0f) throw new ArgumentException("A clip's duration is positive.", nameof(duration));
        if (trackCount < 0 || trackCount > Skeleton.MaxJoints) throw new ArgumentException($"{trackCount} tracks is outside 0..{Skeleton.MaxJoints}.", nameof(trackCount));
        if (timepoints.Length > ushort.MaxValue) throw new ArgumentException("A clip holds at most 65535 distinct key times.", nameof(timepoints));
        var padded = PaddedTrackCount(trackCount);
        translations.Check("translation", padded, timepoints.Length);
        rotations.Check("rotation", padded, timepoints.Length);
        scales.Check("scale", padded, timepoints.Length);

        Name = name;
        Duration = duration;
        TrackCount = trackCount;
        Timepoints = timepoints;
        Translations = translations;
        Rotations = rotations;
        Scales = scales;
    }

    public string Name { get; }

    /// <summary>Seconds; sampling takes a ratio of it.</summary>
    public float Duration { get; }

    public int TrackCount { get; }

    /// <summary>Every distinct key time as a ratio of <see cref="Duration"/>, ascending; keys index into it.</summary>
    public float[] Timepoints { get; }

    public KeyframeStream Translations { get; }

    public KeyframeStream Rotations { get; }

    public KeyframeStream Scales { get; }

    /// <summary>ozz samples four tracks at once, so every stream carries identity keys up to the next multiple of four.</summary>
    public static int PaddedTrackCount(int trackCount) => (trackCount + 3) & ~3;

    public static bool IsAnimation(ReadOnlySpan<byte> bytes) => OzzReader.HasTag(bytes, Tag);

    /// <exception cref="InvalidDataException">Not a version-7 ozz animation archive, or one whose streams are inconsistent.</exception>
    public static AnimationClip Load(ReadOnlySpan<byte> bytes)
    {
        var reader = OzzReader.Open(bytes, Tag, Version);
        var duration = reader.ReadSingle();
        var trackCount = reader.ReadInt32();
        var nameLength = reader.ReadInt32();
        var timepointCount = reader.ReadInt32();
        var translationCount = reader.ReadInt32();
        var rotationCount = reader.ReadInt32();
        var scaleCount = reader.ReadInt32();
        var tEntries = reader.ReadInt32();
        var tDesc = reader.ReadInt32();
        var rEntries = reader.ReadInt32();
        var rDesc = reader.ReadInt32();
        var sEntries = reader.ReadInt32();
        var sDesc = reader.ReadInt32();
        if (nameLength < 0 || timepointCount < 0 || translationCount < 0 || rotationCount < 0 || scaleCount < 0
            || tEntries < 0 || tDesc < 0 || rEntries < 0 || rDesc < 0 || sEntries < 0 || sDesc < 0)
        {
            throw new InvalidDataException("The ozz animation header carries a negative count.");
        }

        var name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
        var timepoints = reader.ReadSingles(timepointCount);
        var ratioBytes = timepointCount <= byte.MaxValue ? 1 : 2;
        var translations = KeyframeStream.Read(ref reader, translationCount, ratioBytes, tEntries, tDesc);
        var rotations = KeyframeStream.Read(ref reader, rotationCount, ratioBytes, rEntries, rDesc);
        var scales = KeyframeStream.Read(ref reader, scaleCount, ratioBytes, sEntries, sDesc);
        reader.ExpectEnd("animation");
        try
        {
            return new AnimationClip(name, duration, trackCount, timepoints, translations, rotations, scales);
        }
        catch (ArgumentException failure)
        {
            throw new InvalidDataException($"The ozz animation is inconsistent: {failure.Message}");
        }
    }

    public byte[] Save()
    {
        var writer = new OzzWriter(Tag, Version);
        var name = Encoding.UTF8.GetBytes(Name);
        writer.Write(Duration);
        writer.Write(TrackCount);
        writer.Write(name.Length);
        writer.Write(Timepoints.Length);
        writer.Write(Translations.KeyCount);
        writer.Write(Rotations.KeyCount);
        writer.Write(Scales.KeyCount);
        writer.Write(Translations.IframeEntries.Length);
        writer.Write(Translations.IframeDesc.Length);
        writer.Write(Rotations.IframeEntries.Length);
        writer.Write(Rotations.IframeDesc.Length);
        writer.Write(Scales.IframeEntries.Length);
        writer.Write(Scales.IframeDesc.Length);
        writer.Write(name);
        writer.Write(Timepoints);
        Translations.Write(writer);
        Rotations.Write(writer);
        Scales.Write(writer);
        return writer.ToArray();
    }
}

/// <summary>
/// One component's keys across every track, in time order. <see cref="Ratios"/> indexes the clip's
/// timepoints (one byte per key when there are at most 256 timepoints, else two);
/// <see cref="Previouses"/> is each key's distance back to the previous key of the same track;
/// <see cref="Values"/> holds three 16-bit words per key. I-frames are group-varint snapshots of
/// the sampler's per-track cursor at regular intervals, so a seek need not walk from the start.
/// </summary>
public sealed class KeyframeStream
{
    public KeyframeStream(byte[] ratios, ushort[] previouses, ushort[] values, byte[] iframeEntries, uint[] iframeDesc, float iframeInterval)
    {
        Ratios = ratios ?? throw new ArgumentNullException(nameof(ratios));
        Previouses = previouses ?? throw new ArgumentNullException(nameof(previouses));
        Values = values ?? throw new ArgumentNullException(nameof(values));
        IframeEntries = iframeEntries ?? throw new ArgumentNullException(nameof(iframeEntries));
        IframeDesc = iframeDesc ?? throw new ArgumentNullException(nameof(iframeDesc));
        IframeInterval = iframeInterval;
    }

    public byte[] Ratios { get; }

    public ushort[] Previouses { get; }

    public ushort[] Values { get; }

    public byte[] IframeEntries { get; }

    /// <summary>Pairs of (byte offset into <see cref="IframeEntries"/>, index of the last key the snapshot covers).</summary>
    public uint[] IframeDesc { get; }

    /// <summary>Spacing of the i-frames as a ratio of the clip; 1 when there are none.</summary>
    public float IframeInterval { get; }

    public int KeyCount => Previouses.Length;

    public int RatioBytes => KeyCount == 0 ? 1 : Ratios.Length / KeyCount;

    /// <summary>The timepoint index of key <paramref name="key"/>.</summary>
    public int TimepointOf(int key) => RatioBytes == 1 ? Ratios[key] : Ratios[key * 2] | (Ratios[key * 2 + 1] << 8);

    internal static KeyframeStream Read(ref OzzReader reader, int keyCount, int ratioBytes, int iframeEntryCount, int iframeDescCount)
    {
        var ratios = reader.ReadBytes(keyCount * ratioBytes).ToArray();
        var previouses = reader.ReadUInt16s(keyCount);
        var iframeEntries = reader.ReadBytes(iframeEntryCount).ToArray();
        var iframeDesc = reader.ReadUInt32s(iframeDescCount);
        var interval = reader.ReadSingle();
        var values = reader.ReadUInt16s(keyCount * 3);
        return new KeyframeStream(ratios, previouses, values, iframeEntries, iframeDesc, interval);
    }

    internal void Write(OzzWriter writer)
    {
        writer.Write(Ratios);
        writer.Write(Previouses);
        writer.Write(IframeEntries);
        writer.Write(IframeDesc);
        writer.Write(IframeInterval);
        writer.Write(Values);
    }

    internal void Check(string component, int paddedTracks, int timepointCount)
    {
        var ratioBytes = timepointCount <= byte.MaxValue ? 1 : 2;
        if (Ratios.Length != KeyCount * ratioBytes) throw new ArgumentException($"The {component} stream has {KeyCount} keys and {Ratios.Length} ratio bytes.");
        if (Values.Length != KeyCount * 3) throw new ArgumentException($"The {component} stream has {KeyCount} keys and {Values.Length} value words.");
        if (paddedTracks > 0 && KeyCount < paddedTracks * 2) throw new ArgumentException($"The {component} stream has {KeyCount} keys; every one of {paddedTracks} tracks needs a first and a last key.");
        if (IframeDesc.Length % 2 != 0) throw new ArgumentException($"The {component} stream's i-frame table has an odd length.");
        for (var i = 0; i < KeyCount; i++)
        {
            if (TimepointOf(i) >= timepointCount) throw new ArgumentException($"The {component} key {i} names timepoint {TimepointOf(i)} of {timepointCount}.");
            if (Previouses[i] > i) throw new ArgumentException($"The {component} key {i} links {Previouses[i]} keys back, before the stream's start.");
        }
    }
}
