using System.Text;

using Paradise.BLOB;

namespace Paradise.Animation;

/// <summary>Which local-transform component a channel drives.</summary>
public enum ChannelPath : byte
{
    Translation = 0,
    Rotation = 1,
    Scale = 2,
}

/// <summary>One keyframe track as a GLB carries it: a joint, a path, interpolation, and packed keys — 3 floats per key for translation and scale, 4 (XYZW) for rotation. <c>Step</c> is STEP interpolation, else LINEAR; CUBICSPLINE is rejected at extraction.</summary>
public readonly record struct ClipChannelData(int Joint, ChannelPath Path, bool Step, float[] Times, float[] Values)
{
    public int FloatsPerKey => Path == ChannelPath.Rotation ? 4 : 3;
}

/// <summary>A clip as cooked from a GLB, channels addressing the joints of the skeleton it was cooked with: the managed shape <see cref="ClipFormat"/> builds a <see cref="ClipBlob"/> from, and what <see cref="Offline.ClipConverter"/> turns into a raw animation.</summary>
public sealed record ClipData(string Name, IReadOnlyList<ClipChannelData> Channels)
{
    public float Duration
    {
        get
        {
            var end = 0f;
            foreach (var channel in Channels)
            {
                if (channel.Times.Length > 0) end = Math.Max(end, channel.Times[^1]);
            }

            return end;
        }
    }
}

/// <summary>A <see cref="ClipChannelData"/> in a blob.</summary>
public struct ClipChannelBlob
{
    public int Joint;
    public ChannelPath Path;
    public bool Step;
    public BlobArray<float> Times;
    public BlobArray<float> Values;

    public int KeyCount => Times.Length;

    public readonly int FloatsPerKey => Path == ChannelPath.Rotation ? 4 : 3;
}

/// <summary>
/// A <see cref="ClipData"/> as one blob: the source-fidelity keys of a clip before ozz
/// compression, deterministic for a given source so its bytes are what the pipeline fingerprints
/// a clip by (name excluded) and finds it again by after the DCC renamed it.
/// </summary>
public struct ClipBlob
{
    public const uint ExpectedMagic = 0x4D4E4150;   // "PANM"

    public const uint ExpectedVersion = 2;

    public uint Magic;
    public uint Version;
    public float Duration;
    public BlobString<UTF8Encoding> Name;
    public BlobArray<ClipChannelBlob> Channels;
}

/// <summary>Builds and opens clip blobs.</summary>
public static class ClipFormat
{
    /// <exception cref="ArgumentException">A channel whose values do not match its keys.</exception>
    public static byte[] Write(ClipData clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        foreach (var channel in clip.Channels)
        {
            if (channel.Values.Length != channel.Times.Length * channel.FloatsPerKey)
            {
                throw new ArgumentException($"Channel on joint {channel.Joint} has {channel.Times.Length} keys and {channel.Values.Length} values; expected {channel.Times.Length * channel.FloatsPerKey}.", nameof(clip));
            }
        }

        var builder = new StructBuilder<ClipBlob>();
        builder.Value.Magic = ClipBlob.ExpectedMagic;
        builder.Value.Version = ClipBlob.ExpectedVersion;
        builder.Value.Duration = clip.Duration;
        builder.SetString(ref builder.Value.Name, clip.Name);
        builder.SetArray(ref builder.Value.Channels, clip.Channels.Select(Channel));
        return builder.CreateBlob();
    }

    public static bool IsClip(ReadOnlySpan<byte> bytes) => bytes.Length >= 8 && BitConverter.ToUInt32(bytes) == ClipBlob.ExpectedMagic;

    /// <exception cref="InvalidDataException">The bytes are not a clip blob this build reads.</exception>
    public static NativeBlobAssetReference<ClipBlob> Open(ReadOnlySpan<byte> bytes)
    {
        if (!IsClip(bytes)) throw new InvalidDataException("Not a Paradise clip blob (bad magic).");
        var reference = new NativeBlobAssetReference<ClipBlob>(bytes);
        try
        {
            ref var blob = ref reference.Value;
            if (blob.Version != ClipBlob.ExpectedVersion) throw new InvalidDataException($"Clip blob version {blob.Version} is not readable by this build (supports {ClipBlob.ExpectedVersion}).");
            for (var i = 0; i < blob.Channels.Length; i++)
            {
                ref var channel = ref blob.Channels[i];
                if (channel.Values.Length != channel.Times.Length * channel.FloatsPerKey) throw new InvalidDataException($"Channel {i} has {channel.Times.Length} keys and {channel.Values.Length} values.");
            }
        }
        catch
        {
            reference.Dispose();
            throw;
        }

        return reference;
    }

    /// <summary>The blob back as managed data — for tests and tools, not the hot path.</summary>
    public static ClipData Read(ReadOnlySpan<byte> bytes)
    {
        using var reference = Open(bytes);
        ref var blob = ref reference.Value;
        var channels = new ClipChannelData[blob.Channels.Length];
        for (var i = 0; i < channels.Length; i++)
        {
            ref var channel = ref blob.Channels[i];
            channels[i] = new ClipChannelData(channel.Joint, channel.Path, channel.Step, channel.Times.ToArray(), channel.Values.ToArray());
        }

        return new ClipData(blob.Name.ToString(), channels);
    }

    private static IBuilder<ClipChannelBlob> Channel(ClipChannelData data)
    {
        var builder = new StructBuilder<ClipChannelBlob>();
        builder.Value.Joint = data.Joint;
        builder.Value.Path = data.Path;
        builder.Value.Step = data.Step;
        builder.SetArray(ref builder.Value.Times, data.Times);
        builder.SetArray(ref builder.Value.Values, data.Values);
        return builder;
    }
}
