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

/// <summary>A clip as cooked from a GLB, channels addressing the joints of the <see cref="Skeleton"/> it was cooked with; what the pipeline fingerprints and <see cref="Offline.ClipConverter"/> turns into a <see cref="Offline.RawAnimation"/>.</summary>
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
