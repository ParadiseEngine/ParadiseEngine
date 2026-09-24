using System.Numerics;

namespace Paradise.Physics;

/// <summary>
/// Rigid-body pose: rotation followed by translation. Right-handed, Y-up, meters
/// (Godot/glTF convention). Scale is not supported — fold scale into geometry before building.
/// </summary>
public struct RigidTransform : IEquatable<RigidTransform>
{
    public Quaternion Rotation;
    public Vector3 Position;

    public static readonly RigidTransform Identity = new(Vector3.Zero, Quaternion.Identity);

    public RigidTransform(Vector3 position, Quaternion rotation)
    {
        Position = position;
        Rotation = rotation;
    }

    public readonly Vector3 Transform(Vector3 point)
        => Vector3.Transform(point, Rotation) + Position;

    public readonly Vector3 InverseTransform(Vector3 point)
        => Vector3.Transform(point - Position, Quaternion.Conjugate(Rotation));

    public readonly Vector3 TransformDirection(Vector3 direction)
        => Vector3.Transform(direction, Rotation);

    public readonly Vector3 InverseTransformDirection(Vector3 direction)
        => Vector3.Transform(direction, Quaternion.Conjugate(Rotation));

    /// <summary>Composition: applying the result equals applying <paramref name="b"/> then <paramref name="a"/>.</summary>
    public static RigidTransform Mul(in RigidTransform a, in RigidTransform b)
        => new(a.Transform(b.Position), Quaternion.Concatenate(b.Rotation, a.Rotation));

    public readonly RigidTransform Inverse()
    {
        var invRotation = Quaternion.Conjugate(Rotation);
        return new RigidTransform(Vector3.Transform(-Position, invRotation), invRotation);
    }

    public readonly bool Equals(RigidTransform other)
        => Rotation.Equals(other.Rotation) && Position.Equals(other.Position);

    public override readonly bool Equals(object? obj) => obj is RigidTransform other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Rotation, Position);

    public static bool operator ==(RigidTransform left, RigidTransform right) => left.Equals(right);

    public static bool operator !=(RigidTransform left, RigidTransform right) => !left.Equals(right);
}

/// <summary>Axis-aligned bounding box in world space.</summary>
public struct Aabb
{
    public Vector3 Min;
    public Vector3 Max;

    public Aabb(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    public readonly bool Overlaps(in Aabb other)
        => Min.X <= other.Max.X && Max.X >= other.Min.X
        && Min.Y <= other.Max.Y && Max.Y >= other.Min.Y
        && Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;

    public void Include(in Aabb other)
    {
        Min = Vector3.Min(Min, other.Min);
        Max = Vector3.Max(Max, other.Max);
    }

    public readonly Aabb Expanded(Vector3 amount)
    {
        var abs = Vector3.Abs(amount);
        return new Aabb(Min - abs, Max + abs);
    }

    /// <summary>
    /// Slab test against the segment <paramref name="start"/> → start + <paramref name="displacement"/>.
    /// Returns true when the segment touches the box within [0, 1]; <paramref name="tMin"/> is the
    /// entry fraction (0 when the segment starts inside).
    /// </summary>
    public readonly bool IntersectsSegment(Vector3 start, Vector3 displacement, out float tMin)
    {
        tMin = 0f;
        float tMax = 1f;
        for (int axis = 0; axis < 3; axis++)
        {
            float s = Axis(start, axis);
            float d = Axis(displacement, axis);
            float lo = Axis(Min, axis);
            float hi = Axis(Max, axis);
            if (MathF.Abs(d) < 1e-12f)
            {
                if (s < lo || s > hi) return false;
                continue;
            }

            float inv = 1f / d;
            float t1 = (lo - s) * inv;
            float t2 = (hi - s) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tMin) tMin = t1;
            if (t2 < tMax) tMax = t2;
            if (tMin > tMax) return false;
        }

        return true;

        static float Axis(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
    }
}
