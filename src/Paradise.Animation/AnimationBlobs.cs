using System.Numerics;
using System.Text;

using Paradise.BLOB;

namespace Paradise.Animation;

/// <summary>One node of the rig: its parent (−1 for a scene root), its name, and its rest-pose local transform.</summary>
public struct SkeletonNode
{
    public int Parent;
    public Vector3 RestTranslation;
    public Quaternion RestRotation;
    public Vector3 RestScale;
    public BlobString<UTF8Encoding> Name;
}

/// <summary>One skin: which nodes are its joints, in palette order, and their inverse-bind matrices in the same order (row-vector convention).</summary>
public struct SkinBlob
{
    public BlobString<UTF8Encoding> Name;
    public BlobArray<int> JointNodes;
    public BlobArray<Matrix4x4> InverseBindMatrices;

    public int JointCount => JointNodes.Length;
}

/// <summary>
/// A rig as a GLB's default scene carries it: the WHOLE node tree with rest pose, plus its skins.
/// Clips address nodes by index into <see cref="Nodes"/>; a mesh draw addresses a skin by index
/// into <see cref="Skins"/> — a clip depends on a skeleton, never on a mesh, and two clips cannot
/// disagree about the joint order because neither carries it.
/// </summary>
/// <remarks>
/// The tree is kept whole rather than trimmed to joints because glTF clips may animate a node that
/// is not a joint (a root-motion carrier, an attachment point), and the palette is a product of
/// the animated WORLD pose, which needs every ancestor. A <c>Paradise.BLOB</c> layout: one
/// aligned native copy, never parsed. Magic and version first, so a reader refuses a foreign or newer blob before
/// touching an offset.
/// </remarks>
public struct SkeletonBlob
{
    public const uint ExpectedMagic = 0x4C4B5350;   // "PSKL"

    public const uint ExpectedVersion = 1;

    public uint Magic;
    public uint Version;
    public BlobArray<SkeletonNode> Nodes;
    public BlobArray<SkinBlob> Skins;

    /// <summary>The first node named <paramref name="name"/>, or −1. Ordinal-ignore-case, like a clip lookup. Not <c>readonly</c>: a BlobArray reached through a readonly reference is a defensive copy whose relative offset points nowhere.</summary>
    public int FindNode(string name)
    {
        for (var i = 0; i < Nodes.Length; i++)
        {
            if (string.Equals(Nodes[i].Name.ToString(), name, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }
}

/// <summary>Which local-transform component a channel drives.</summary>
public enum ChannelPath : byte
{
    Translation = 0,
    Rotation = 1,
    Scale = 2,
}

/// <summary>One keyframe track: node, path, interpolation, and packed keys — 3 floats per key for translation and scale, 4 (XYZW) for rotation. <c>Step</c> is STEP interpolation, else LINEAR (lerp; slerp for rotation); CUBICSPLINE is rejected at extraction.</summary>
public struct ClipChannel
{
    public int Node;
    public ChannelPath Path;
    public bool Step;
    public BlobArray<float> Times;
    public BlobArray<float> Values;

    public int KeyCount => Times.Length;

    public readonly int FloatsPerKey => Path == ChannelPath.Rotation ? 4 : 3;
}

/// <summary>One animation clip over a <see cref="SkeletonBlob"/>'s nodes. Times are seconds, ascending; unanimated nodes keep their rest pose (glTF semantics).</summary>
public struct ClipBlob
{
    public const uint ExpectedMagic = 0x4D4E4150;   // "PANM"

    public const uint ExpectedVersion = 1;

    public uint Magic;
    public uint Version;
    public float Duration;
    public BlobString<UTF8Encoding> Name;
    public BlobArray<ClipChannel> Channels;
}

// ---- managed shapes: what the pipeline builds from, what a test reads back into --------------

public readonly record struct SkeletonNodeData(string? Name, int Parent, Vector3 RestTranslation, Quaternion RestRotation, Vector3 RestScale);

public sealed record SkinData(string? Name, int[] JointNodes, Matrix4x4[] InverseBindMatrices);

public sealed record SkeletonData(IReadOnlyList<SkeletonNodeData> Nodes, IReadOnlyList<SkinData> Skins);

public readonly record struct ClipChannelData(int Node, ChannelPath Path, bool Step, float[] Times, float[] Values)
{
    public int FloatsPerKey => Path == ChannelPath.Rotation ? 4 : 3;
}

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

/// <summary>Builds and opens skeleton blobs.</summary>
public static class SkeletonFormat
{
    public static byte[] Write(SkeletonData skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        foreach (var skin in skeleton.Skins)
        {
            if (skin.InverseBindMatrices.Length != skin.JointNodes.Length)
            {
                throw new ArgumentException($"Skin '{skin.Name}' has {skin.JointNodes.Length} joints and {skin.InverseBindMatrices.Length} inverse-bind matrices.", nameof(skeleton));
            }
        }

        var builder = new StructBuilder<SkeletonBlob>();
        builder.Value.Magic = SkeletonBlob.ExpectedMagic;
        builder.Value.Version = SkeletonBlob.ExpectedVersion;
        builder.SetArray(ref builder.Value.Nodes, skeleton.Nodes.Select(Node));
        builder.SetArray(ref builder.Value.Skins, skeleton.Skins.Select(Skin));
        return builder.CreateBlob();
    }

    public static bool IsSkeleton(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 8 && BitConverter.ToUInt32(bytes) == SkeletonBlob.ExpectedMagic;

    /// <exception cref="InvalidDataException">The bytes are not a skeleton blob this build reads.</exception>
    public static NativeBlobAssetReference<SkeletonBlob> Open(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 || BitConverter.ToUInt32(bytes) != SkeletonBlob.ExpectedMagic) throw new InvalidDataException("Not a Paradise skeleton blob (bad magic).");
        var reference = new NativeBlobAssetReference<SkeletonBlob>(bytes);
        try
        {
            ref var blob = ref reference.Value;
            if (blob.Version != SkeletonBlob.ExpectedVersion) throw new InvalidDataException($"Skeleton blob version {blob.Version} is not readable by this build (supports {SkeletonBlob.ExpectedVersion}).");
            for (var i = 0; i < blob.Nodes.Length; i++)
            {
                var parent = blob.Nodes[i].Parent;
                if (parent < -1 || parent >= blob.Nodes.Length) throw new InvalidDataException($"Node {i} has parent {parent}, outside the tree.");
            }

            for (var i = 0; i < blob.Skins.Length; i++)
            {
                ref var skin = ref blob.Skins[i];
                if (skin.InverseBindMatrices.Length != skin.JointNodes.Length) throw new InvalidDataException($"Skin {i} has {skin.JointNodes.Length} joints and {skin.InverseBindMatrices.Length} inverse-bind matrices.");
                for (var j = 0; j < skin.JointNodes.Length; j++)
                {
                    if (skin.JointNodes[j] < 0 || skin.JointNodes[j] >= blob.Nodes.Length) throw new InvalidDataException($"Skin {i} joint {j} names node {skin.JointNodes[j]}, outside the tree.");
                }
            }
        }
        catch
        {
            reference.Dispose();
            throw;
        }

        return reference;
    }

    /// <summary>The blob back as managed data — for tests and tools.</summary>
    public static SkeletonData Read(byte[] bytes)
    {
        using var reference = Open(bytes);
        ref var blob = ref reference.Value;
        var nodes = new SkeletonNodeData[blob.Nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            ref var node = ref blob.Nodes[i];
            // In place, never through a copy: a BlobString passed by value carries a relative
            // offset that no longer points into the blob.
            nodes[i] = new SkeletonNodeData(node.Name.Length == 0 ? null : node.Name.ToString(), node.Parent, node.RestTranslation, node.RestRotation, node.RestScale);
        }

        var skins = new SkinData[blob.Skins.Length];
        for (var i = 0; i < skins.Length; i++)
        {
            ref var skin = ref blob.Skins[i];
            skins[i] = new SkinData(skin.Name.Length == 0 ? null : skin.Name.ToString(), skin.JointNodes.ToArray(), skin.InverseBindMatrices.ToArray());
        }

        return new SkeletonData(nodes, skins);
    }

    private static IBuilder<SkeletonNode> Node(SkeletonNodeData data)
    {
        var builder = new StructBuilder<SkeletonNode>();
        builder.Value.Parent = data.Parent;
        builder.Value.RestTranslation = data.RestTranslation;
        builder.Value.RestRotation = data.RestRotation;
        builder.Value.RestScale = data.RestScale;
        builder.SetString(ref builder.Value.Name, data.Name ?? string.Empty);
        return builder;
    }

    private static IBuilder<SkinBlob> Skin(SkinData data)
    {
        var builder = new StructBuilder<SkinBlob>();
        builder.SetString(ref builder.Value.Name, data.Name ?? string.Empty);
        builder.SetArray(ref builder.Value.JointNodes, data.JointNodes);
        builder.SetArray(ref builder.Value.InverseBindMatrices, data.InverseBindMatrices);
        return builder;
    }
}

/// <summary>Builds and opens clip blobs.</summary>
public static class ClipFormat
{
    public static byte[] Write(ClipData clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        foreach (var channel in clip.Channels)
        {
            if (channel.Values.Length != channel.Times.Length * channel.FloatsPerKey)
            {
                throw new ArgumentException($"Channel on node {channel.Node} has {channel.Times.Length} keys and {channel.Values.Length} values; expected {channel.Times.Length * channel.FloatsPerKey}.", nameof(clip));
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

    public static bool IsClip(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 8 && BitConverter.ToUInt32(bytes) == ClipBlob.ExpectedMagic;

    /// <exception cref="InvalidDataException">The bytes are not a clip blob this build reads.</exception>
    public static NativeBlobAssetReference<ClipBlob> Open(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 || BitConverter.ToUInt32(bytes) != ClipBlob.ExpectedMagic) throw new InvalidDataException("Not a Paradise clip blob (bad magic).");
        var reference = new NativeBlobAssetReference<ClipBlob>(bytes);
        try
        {
            ref var blob = ref reference.Value;
            if (blob.Version != ClipBlob.ExpectedVersion) throw new InvalidDataException($"Clip blob version {blob.Version} is not readable by this build (supports {ClipBlob.ExpectedVersion}).");
            for (var i = 0; i < blob.Channels.Length; i++)
            {
                ref var channel = ref blob.Channels[i];
                if (channel.Path is not (ChannelPath.Translation or ChannelPath.Rotation or ChannelPath.Scale)) throw new InvalidDataException($"Channel {i} has unknown path {(int)channel.Path}.");
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

    /// <summary>The blob back as managed data — for tests and tools.</summary>
    public static ClipData Read(byte[] bytes)
    {
        using var reference = Open(bytes);
        ref var blob = ref reference.Value;
        var channels = new ClipChannelData[blob.Channels.Length];
        for (var i = 0; i < channels.Length; i++)
        {
            ref var channel = ref blob.Channels[i];
            channels[i] = new ClipChannelData(channel.Node, channel.Path, channel.Step, channel.Times.ToArray(), channel.Values.ToArray());
        }

        return new ClipData(blob.Name.ToString(), channels);
    }

    private static IBuilder<ClipChannel> Channel(ClipChannelData data)
    {
        var builder = new StructBuilder<ClipChannel>();
        builder.Value.Node = data.Node;
        builder.Value.Path = data.Path;
        builder.Value.Step = data.Step;
        builder.SetArray(ref builder.Value.Times, data.Times);
        builder.SetArray(ref builder.Value.Values, data.Values);
        return builder;
    }
}
