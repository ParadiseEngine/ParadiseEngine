using System.Numerics;
using System.Text;

using Paradise.BLOB;

namespace Paradise.Animation;

/// <summary>One joint's local transform: what a clip samples to and a rest pose holds.</summary>
public readonly record struct JointPose(Vector3 Translation, Quaternion Rotation, Vector3 Scale)
{
    public static JointPose Identity { get; } = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>Row-vector convention: scale, then rotate, then translate.</summary>
    public Matrix4x4 ToMatrix() =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Translation);
}

/// <summary>
/// An ozz-animation skeleton as one native blob: joints in depth-first order, each with its
/// parent index, its name and its rest-pose local transform. A parent always precedes its
/// children, so a single forward pass computes model-space poses. Opened from an
/// <c>ozz-skeleton</c> archive by <see cref="OzzArchive.ReadSkeleton(System.ReadOnlySpan{byte})"/>, or built by the offline
/// side; read only through a <c>ref</c>, never through a copy.
/// </summary>
public struct SkeletonBlob
{
    /// <summary>ozz's limit; a clip's track index and the sampler's cache are 16-bit.</summary>
    public const int MaxJoints = 1024;

    public const short NoParent = -1;

    public BlobArray<BlobString<UTF8Encoding>> Names;
    public BlobArray<short> Parents;
    public BlobArray<JointPose> RestPoses;

    public int JointCount => Parents.Length;

    /// <summary>The first joint of that exact UTF-8 name, or −1; allocation-free.</summary>
    public int FindJoint(ReadOnlySpan<byte> utf8Name)
    {
        for (var i = 0; i < Names.Length; i++)
        {
            if (Names[i].ToSpan().SequenceEqual(utf8Name)) return i;
        }

        return -1;
    }

    public int FindJoint(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var length = Encoding.UTF8.GetByteCount(name);
        Span<byte> utf8 = length <= 256 ? stackalloc byte[length] : new byte[length];
        Encoding.UTF8.GetBytes(name, utf8);
        return FindJoint(utf8);
    }

    public bool IsLeaf(int joint)
    {
        for (var i = joint + 1; i < Parents.Length; i++)
        {
            if (Parents[i] == joint) return false;
        }

        return true;
    }

    /// <summary>Builds the blob from flat depth-first arrays; what the archive reader, the offline builder and the GLB cook all go through.</summary>
    /// <exception cref="ArgumentException">Mismatched lengths, too many joints, or a parent that does not precede its child.</exception>
    public static NativeBlobAssetReference<SkeletonBlob> Create(string[] names, short[] parents, JointPose[] restPoses)
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

        var builder = new StructBuilder<SkeletonBlob>();
        builder.SetArray(ref builder.Value.Names, names.Select(name => (IBuilder<BlobString<UTF8Encoding>>)new StringBuilder<UTF8Encoding>(name)));
        builder.SetArray(ref builder.Value.Parents, parents);
        builder.SetArray(ref builder.Value.RestPoses, restPoses);
        return builder.CreateNativeBlobAssetReference();
    }
}
