using System.Runtime.Intrinsics;

namespace Paradise.Animation;

/// <summary>Per-joint weights in 0..1 for one skeleton, such as an upper-body mask for an override layer.</summary>
/// <remarks>Build masks while loading and share them; they are immutable. A mask remembers the joint names and
/// hierarchy it was made for, and a player refuses it for any other skeleton. A zero weight excludes a joint; a
/// mask of all zeros is valid and excludes everything, unlike no mask, which includes everything.</remarks>
public sealed class JointMask
{
    private readonly float[] _weights;
    private readonly Vector128<float>[] _groups;

    private JointMask(float[] weights, ulong skeleton)
    {
        _weights = weights;
        SkeletonFingerprint = skeleton;
        _groups = new Vector128<float>[AnimationBlob.PaddedTrackCount(weights.Length) / 4];
        for (var g = 0; g < _groups.Length; g++)
        {
            _groups[g] = Vector128.Create(Lane(weights, g * 4), Lane(weights, g * 4 + 1), Lane(weights, g * 4 + 2), Lane(weights, g * 4 + 3));
        }
    }

    private static float Lane(float[] weights, int joint) => joint < weights.Length ? weights[joint] : 0f;

    public int JointCount => _weights.Length;

    public float this[int joint] => _weights[joint];

    public ReadOnlySpan<float> Weights => _weights;

    internal ulong SkeletonFingerprint { get; }

    /// <summary>The weights four joints to a lane group, padding lanes zero.</summary>
    internal ReadOnlySpan<Vector128<float>> Groups => _groups;

    /// <summary>One weight per joint, in the skeleton's order.</summary>
    /// <exception cref="ArgumentException">Not one weight per joint, or a weight outside 0..1.</exception>
    public static JointMask Create(ref SkeletonBlob skeleton, ReadOnlySpan<float> weights)
    {
        if (weights.Length != skeleton.JointCount) throw new ArgumentException($"{weights.Length} weights for {skeleton.JointCount} joints.", nameof(weights));
        for (var joint = 0; joint < weights.Length; joint++)
        {
            if (!(weights[joint] >= 0f && weights[joint] <= 1f)) throw new ArgumentException($"Joint {joint} has weight {weights[joint]}; a mask weight is in 0..1.", nameof(weights));
        }

        return new JointMask(weights.ToArray(), skeleton.Fingerprint());
    }

    /// <summary>Weight 1 for each named joint and every joint below it, 0 elsewhere.</summary>
    /// <exception cref="ArgumentException">A name the skeleton lacks.</exception>
    public static JointMask Branch(ref SkeletonBlob skeleton, params ReadOnlySpan<string> joints)
    {
        var weights = new float[skeleton.JointCount];
        foreach (var name in joints)
        {
            var root = skeleton.FindJoint(name);
            if (root < 0) throw new ArgumentException($"The skeleton has no joint named '{name}'.", nameof(joints));
            weights[root] = 1f;
        }

        // Parents precede children, so one forward pass carries each branch down to its leaves.
        var parents = skeleton.Parents.ToSpan();
        for (var joint = 0; joint < weights.Length; joint++)
        {
            if (parents[joint] != SkeletonBlob.NoParent && weights[parents[joint]] == 1f) weights[joint] = 1f;
        }

        return new JointMask(weights, skeleton.Fingerprint());
    }

    /// <summary>The mask's lane groups, or an empty span for no mask.</summary>
    /// <exception cref="ArgumentException">The mask covers a different number of joints.</exception>
    internal static ReadOnlySpan<Vector128<float>> GroupsFor(JointMask? mask, int jointCount)
    {
        if (mask is null) return default;
        if (mask.JointCount != jointCount) throw new ArgumentException($"The mask covers {mask.JointCount} joints; the poses hold {jointCount}.", nameof(mask));
        return mask._groups;
    }
}
