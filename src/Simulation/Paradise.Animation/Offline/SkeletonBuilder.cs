using Paradise.BLOB;

namespace Paradise.Animation.Offline;

/// <summary>Flattens a <see cref="RawSkeleton"/> depth-first into a runtime <see cref="SkeletonBlob"/>; ozz's <c>SkeletonBuilder</c>, giving the same joint order and bytes.</summary>
public static class SkeletonBuilder
{
    /// <exception cref="ArgumentException">More joints than ozz allows.</exception>
    public static NativeBlobAssetReference<SkeletonBlob> Build(RawSkeleton raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (!raw.IsValid) throw new ArgumentException($"{raw.JointCount} joints exceed ozz's limit of {SkeletonBlob.MaxJoints}.", nameof(raw));

        var names = new List<string>();
        var parents = new List<short>();
        var poses = new List<JointPose>();
        var indexOf = new Dictionary<RawJoint, short>(ReferenceEqualityComparer.Instance);
        raw.VisitDepthFirst((joint, parent) =>
        {
            indexOf[joint] = (short)names.Count;
            names.Add(joint.Name);
            parents.Add(parent is null ? SkeletonBlob.NoParent : indexOf[parent]);
            poses.Add(joint.Transform.WithNormalizedRotation());
        });
        return SkeletonBlob.Create([.. names], [.. parents], [.. poses]);
    }

}
