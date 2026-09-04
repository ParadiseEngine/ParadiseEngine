using System.Numerics;
using System.Text;

// Frozen copy of the managed-class runtime as committed in e4fa124, kept ONLY so the benchmark
// can measure it against the blob runtime that replaced it. Do not fix or extend; delete when the
// comparison stops being interesting.
namespace Paradise.Animation.Benchmarks.Managed;

using Paradise.Animation;

/// <summary>
/// An ozz-animation runtime skeleton: joints in depth-first order, each with its parent index, its
/// name and its rest-pose local transform. A parent always precedes its children, so a single
/// forward pass computes model-space poses.
/// </summary>
/// <remarks>
/// Reads and writes the <c>ozz-skeleton</c> archive, version 2 (ozz-animation 0.17). The archive
/// stores rest poses in structure-of-arrays groups of four; they are unpacked here to one
/// transform per joint because the sampler in this assembly is scalar.
/// </remarks>
public sealed class Skeleton
{
    public const string Tag = "ozz-skeleton";

    public const uint Version = 2;

    /// <summary>ozz's limit; a clip's track index and the sampler's cache are 16-bit.</summary>
    public const int MaxJoints = 1024;

    public const short NoParent = -1;

    private readonly string[] _names;
    private readonly short[] _parents;
    private readonly JointPose[] _restPoses;

    public Skeleton(string[] names, short[] parents, JointPose[] restPoses)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(parents);
        ArgumentNullException.ThrowIfNull(restPoses);
        if (parents.Length != names.Length || restPoses.Length != names.Length)
        {
            throw new ArgumentException($"{names.Length} names, {parents.Length} parents and {restPoses.Length} rest poses do not describe one skeleton.");
        }

        if (names.Length > MaxJoints) throw new ArgumentException($"{names.Length} joints exceed ozz's limit of {MaxJoints}.");
        for (var i = 0; i < parents.Length; i++)
        {
            if (parents[i] != NoParent && (parents[i] < 0 || parents[i] >= i))
            {
                throw new ArgumentException($"Joint {i} has parent {parents[i]}; a parent precedes its child in depth-first order.");
            }
        }

        _names = names;
        _parents = parents;
        _restPoses = restPoses;
    }

    public int JointCount => _names.Length;

    public ReadOnlySpan<string> Names => _names;

    public ReadOnlySpan<short> Parents => _parents;

    public ReadOnlySpan<JointPose> RestPoses => _restPoses;

    /// <summary>The first joint of that exact name, or −1.</summary>
    public int FindJoint(string name) => Array.IndexOf(_names, name);

    public bool IsLeaf(int joint)
    {
        for (var i = joint + 1; i < _parents.Length; i++)
        {
            if (_parents[i] == joint) return false;
        }

        return true;
    }

    public static bool IsSkeleton(ReadOnlySpan<byte> bytes) => OzzReader.HasTag(bytes, Tag);

    /// <exception cref="InvalidDataException">Not a version-2 ozz skeleton archive, or one whose joints do not form a depth-first tree.</exception>
    public static Skeleton Load(ReadOnlySpan<byte> bytes)
    {
        var reader = OzzReader.Open(bytes, Tag, Version);
        var count = reader.ReadInt32();
        if (count == 0)
        {
            reader.ExpectEnd("skeleton");
            return new Skeleton([], [], []);
        }

        if (count < 0 || count > MaxJoints) throw new InvalidDataException($"The ozz skeleton names {count} joints; the limit is {MaxJoints}.");
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
            if (parents[i] != NoParent && (parents[i] < 0 || parents[i] >= i))
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

        return new Skeleton(names, parents, poses);
    }

    public byte[] Save()
    {
        var writer = new OzzWriter(Tag, Version);
        writer.Write(JointCount);
        if (JointCount == 0) return writer.ToArray();

        var chars = new MemoryStream();
        foreach (var name in _names)
        {
            chars.Write(Encoding.UTF8.GetBytes(name));
            chars.WriteByte(0);
        }

        writer.Write((int)chars.Length);
        writer.Write(chars.ToArray());
        writer.Write(_parents);
        var groups = (JointCount + 3) / 4;
        var soa = new float[groups * SoaFloatsPerGroup];
        for (var i = 0; i < groups * 4; i++)
        {
            var pose = i < JointCount ? _restPoses[i] : JointPose.Identity;
            var g = (i / 4) * SoaFloatsPerGroup;
            var lane = i % 4;
            soa[g + lane] = pose.Translation.X; soa[g + 4 + lane] = pose.Translation.Y; soa[g + 8 + lane] = pose.Translation.Z;
            soa[g + 12 + lane] = pose.Rotation.X; soa[g + 16 + lane] = pose.Rotation.Y; soa[g + 20 + lane] = pose.Rotation.Z; soa[g + 24 + lane] = pose.Rotation.W;
            soa[g + 28 + lane] = pose.Scale.X; soa[g + 32 + lane] = pose.Scale.Y; soa[g + 36 + lane] = pose.Scale.Z;
        }

        writer.Write(soa);
        return writer.ToArray();
    }

    // translation xyz, rotation xyzw, scale xyz: ten SIMD lanes of four floats.
    private const int SoaFloatsPerGroup = 40;
}

