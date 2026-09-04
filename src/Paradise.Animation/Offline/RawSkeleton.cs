using System.Numerics;

namespace Paradise.Animation.Offline;

/// <summary>The authoring-side skeleton: a tree of named joints with rest-pose transforms, what <see cref="SkeletonBuilder"/> flattens into a runtime <see cref="Skeleton"/>.</summary>
public sealed class RawSkeleton
{
    public List<RawJoint> Roots { get; } = [];

    public int JointCount
    {
        get
        {
            var count = 0;
            foreach (var root in Roots) count += root.SubtreeCount;
            return count;
        }
    }

    /// <summary>At most <see cref="Skeleton.MaxJoints"/> joints; nothing else can be wrong with a tree.</summary>
    public bool IsValid => JointCount <= Skeleton.MaxJoints;

    /// <summary>Visits joints depth-first, parents before children, siblings in order — the runtime joint order.</summary>
    public void VisitDepthFirst(Action<RawJoint, RawJoint?> visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        foreach (var root in Roots) Visit(root, null, visit);
    }

    private static void Visit(RawJoint joint, RawJoint? parent, Action<RawJoint, RawJoint?> visit)
    {
        visit(joint, parent);
        foreach (var child in joint.Children) Visit(child, joint, visit);
    }
}

public sealed class RawJoint(string name)
{
    public string Name { get; set; } = name ?? throw new ArgumentNullException(nameof(name));

    public JointPose Transform { get; set; } = JointPose.Identity;

    public List<RawJoint> Children { get; } = [];

    internal int SubtreeCount
    {
        get
        {
            var count = 1;
            foreach (var child in Children) count += child.SubtreeCount;
            return count;
        }
    }
}
