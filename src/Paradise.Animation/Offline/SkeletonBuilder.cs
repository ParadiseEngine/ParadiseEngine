using System.Numerics;

namespace Paradise.Animation.Offline;

/// <summary>Flattens a <see cref="RawSkeleton"/> depth-first into a runtime <see cref="Skeleton"/>; ozz's <c>SkeletonBuilder</c>, giving the same joint order and bytes.</summary>
public static class SkeletonBuilder
{
    /// <exception cref="ArgumentException">More joints than ozz allows.</exception>
    public static Skeleton Build(RawSkeleton raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (!raw.IsValid) throw new ArgumentException($"{raw.JointCount} joints exceed ozz's limit of {Skeleton.MaxJoints}.", nameof(raw));

        var names = new List<string>();
        var parents = new List<short>();
        var poses = new List<JointPose>();
        var indexOf = new Dictionary<RawJoint, short>(ReferenceEqualityComparer.Instance);
        raw.VisitDepthFirst((joint, parent) =>
        {
            indexOf[joint] = (short)names.Count;
            names.Add(joint.Name);
            parents.Add(parent is null ? Skeleton.NoParent : indexOf[parent]);
            poses.Add(joint.Transform with { Rotation = NormalizeOrIdentity(joint.Transform.Rotation) });
        });
        return new Skeleton([.. names], [.. parents], [.. poses]);
    }

    private static Quaternion NormalizeOrIdentity(Quaternion q)
    {
        var lengthSquared = q.LengthSquared();
        if (lengthSquared == 0f) return Quaternion.Identity;
        var inverse = 1f / MathF.Sqrt(lengthSquared);
        return new Quaternion(q.X * inverse, q.Y * inverse, q.Z * inverse, q.W * inverse);
    }
}
