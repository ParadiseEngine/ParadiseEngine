using System.Numerics;
using System.Text;

using Paradise.BLOB;

namespace Paradise.Animation;

/// <summary>
/// The persisted form of a skeleton or clip: ozz-animation's archive (one endianness byte, a
/// null-terminated type tag, a uint32 version, then the payload), read into a native blob and
/// written back from one. Little-endian only — the archives this engine cooks and reads are its
/// own, and a big-endian file is refused rather than byte-swapped.
/// </summary>
/// <remarks>
/// Cross-language contract with ozz-animation 0.17 (<c>ozz/base/io/archive.h</c>,
/// <c>skeleton.cc</c>, <c>animation.cc</c>): a file written by <c>gltf2ozz</c> loads here, and a
/// file written here loads in ozz's C++ runtime. The archive stores rest poses in
/// structure-of-arrays groups of four; the blob holds one pose per joint, the shape the
/// hierarchy walk and the renderer consume.
/// </remarks>
public static class OzzArchive
{
    public const string SkeletonTag = "ozz-skeleton";

    public const uint SkeletonVersion = 2;

    public const string AnimationTag = "ozz-animation";

    public const uint AnimationVersion = 7;

    // translation xyz, rotation xyzw, scale xyz: ten SIMD lanes of four floats.
    private const int SoaFloatsPerGroup = 40;

    public static bool IsSkeleton(ReadOnlySpan<byte> bytes) => OzzReader.HasTag(bytes, SkeletonTag);

    public static bool IsAnimation(ReadOnlySpan<byte> bytes) => OzzReader.HasTag(bytes, AnimationTag);

    /// <exception cref="InvalidDataException">Not a version-2 ozz skeleton archive, or one whose joints do not form a depth-first tree.</exception>
    public static NativeBlobAssetReference<SkeletonBlob> ReadSkeleton(ReadOnlySpan<byte> archive)
    {
        var reader = OzzReader.Open(archive, SkeletonTag, SkeletonVersion);
        var count = reader.ReadInt32();
        if (count == 0)
        {
            reader.ExpectEnd("skeleton");
            return SkeletonBlob.Create([], [], []);
        }

        if (count < 0 || count > SkeletonBlob.MaxJoints) throw new InvalidDataException($"The ozz skeleton names {count} joints; the limit is {SkeletonBlob.MaxJoints}.");
        var charCount = reader.ReadInt32();
        if (charCount < count) throw new InvalidDataException("The ozz skeleton's name block is shorter than one terminator per joint.");
        var chars = reader.ReadBytes(charCount);
        var names = new string[count];
        var at = 0;
        for (var i = 0; i < count; i++)
        {
            var end = chars[at..].IndexOf((byte)0);
            if (end < 0) throw new InvalidDataException($"The ozz skeleton's name {i} is not terminated.");
            names[i] = Encoding.UTF8.GetString(chars.Slice(at, end));
            at += end + 1;
        }

        var parents = new short[count];
        for (var i = 0; i < count; i++)
        {
            parents[i] = reader.ReadInt16();
            if (parents[i] != SkeletonBlob.NoParent && (parents[i] < 0 || parents[i] >= i))
            {
                throw new InvalidDataException($"The ozz skeleton's joint {i} has parent {parents[i]}, which does not precede it.");
            }
        }

        var groups = (count + 3) / 4;
        var soa = reader.ReadSingles(groups * SoaFloatsPerGroup);
        reader.ExpectEnd("skeleton");
        var poses = new JointPose[count];
        for (var i = 0; i < count; i++)
        {
            var g = (i / 4) * SoaFloatsPerGroup;
            var lane = i % 4;
            poses[i] = new JointPose(
                new Vector3(soa[g + lane], soa[g + 4 + lane], soa[g + 8 + lane]),
                new Quaternion(soa[g + 12 + lane], soa[g + 16 + lane], soa[g + 20 + lane], soa[g + 24 + lane]),
                new Vector3(soa[g + 28 + lane], soa[g + 32 + lane], soa[g + 36 + lane]));
        }

        return SkeletonBlob.Create(names, parents, poses);
    }

    public static byte[] WriteSkeleton(ref SkeletonBlob skeleton)
    {
        var writer = new OzzWriter(SkeletonTag, SkeletonVersion);
        var count = skeleton.JointCount;
        writer.Write(count);
        if (count == 0) return writer.ToArray();

        var chars = new MemoryStream();
        for (var i = 0; i < count; i++)
        {
            chars.Write(skeleton.Names[i].ToSpan());
            chars.WriteByte(0);
        }

        writer.Write((int)chars.Length);
        writer.Write(chars.ToArray());
        writer.Write(skeleton.Parents.ToSpan());
        var groups = (count + 3) / 4;
        var soa = new float[groups * SoaFloatsPerGroup];
        for (var i = 0; i < groups * 4; i++)
        {
            var pose = i < count ? skeleton.RestPoses[i] : JointPose.Identity;
            var g = (i / 4) * SoaFloatsPerGroup;
            var lane = i % 4;
            soa[g + lane] = pose.Translation.X; soa[g + 4 + lane] = pose.Translation.Y; soa[g + 8 + lane] = pose.Translation.Z;
            soa[g + 12 + lane] = pose.Rotation.X; soa[g + 16 + lane] = pose.Rotation.Y; soa[g + 20 + lane] = pose.Rotation.Z; soa[g + 24 + lane] = pose.Rotation.W;
            soa[g + 28 + lane] = pose.Scale.X; soa[g + 32 + lane] = pose.Scale.Y; soa[g + 36 + lane] = pose.Scale.Z;
        }

        writer.Write(soa);
        return writer.ToArray();
    }

    /// <exception cref="InvalidDataException">Not a version-7 ozz animation archive, or one whose streams are inconsistent.</exception>
    public static NativeBlobAssetReference<AnimationBlob> ReadAnimation(ReadOnlySpan<byte> archive)
    {
        var reader = OzzReader.Open(archive, AnimationTag, AnimationVersion);
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
        var translations = ReadStream(ref reader, translationCount, ratioBytes, tEntries, tDesc);
        var rotations = ReadStream(ref reader, rotationCount, ratioBytes, rEntries, rDesc);
        var scales = ReadStream(ref reader, scaleCount, ratioBytes, sEntries, sDesc);
        reader.ExpectEnd("animation");
        try
        {
            return AnimationBlob.Create(name, duration, trackCount, timepoints, translations, rotations, scales);
        }
        catch (ArgumentException failure)
        {
            throw new InvalidDataException($"The ozz animation is inconsistent: {failure.Message}");
        }
    }

    public static byte[] WriteAnimation(ref AnimationBlob clip)
    {
        var writer = new OzzWriter(AnimationTag, AnimationVersion);
        writer.Write(clip.Duration);
        writer.Write(clip.TrackCount);
        writer.Write(clip.Name.Length);
        writer.Write(clip.Timepoints.Length);
        writer.Write(clip.Translations.KeyCount);
        writer.Write(clip.Rotations.KeyCount);
        writer.Write(clip.Scales.KeyCount);
        writer.Write(clip.Translations.IframeEntries.Length);
        writer.Write(clip.Translations.IframeDesc.Length);
        writer.Write(clip.Rotations.IframeEntries.Length);
        writer.Write(clip.Rotations.IframeDesc.Length);
        writer.Write(clip.Scales.IframeEntries.Length);
        writer.Write(clip.Scales.IframeDesc.Length);
        writer.Write(clip.Name.ToSpan());
        writer.Write(clip.Timepoints.ToSpan());
        WriteStream(writer, ref clip.Translations);
        WriteStream(writer, ref clip.Rotations);
        WriteStream(writer, ref clip.Scales);
        return writer.ToArray();
    }

    private static KeyframeStreamData ReadStream(ref OzzReader reader, int keyCount, int ratioBytes, int iframeEntryCount, int iframeDescCount)
    {
        var ratios = reader.ReadBytes(keyCount * ratioBytes).ToArray();
        var previouses = reader.ReadUInt16s(keyCount);
        var iframeEntries = reader.ReadBytes(iframeEntryCount).ToArray();
        var iframeDesc = reader.ReadUInt32s(iframeDescCount);
        var interval = reader.ReadSingle();
        var values = reader.ReadUInt16s(keyCount * 3);
        return new KeyframeStreamData(ratios, previouses, values, iframeEntries, iframeDesc, interval);
    }

    private static void WriteStream(OzzWriter writer, ref KeyframeStreamBlob stream)
    {
        writer.Write(stream.Ratios.ToSpan());
        writer.Write(stream.Previouses.ToSpan());
        writer.Write(stream.IframeEntries.ToSpan());
        writer.Write(stream.IframeDesc.ToSpan());
        writer.Write(stream.IframeInterval);
        writer.Write(stream.Values.ToSpan());
    }
}
