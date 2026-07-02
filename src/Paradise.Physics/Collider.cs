using System.Numerics;
using System.Runtime.InteropServices;

namespace Paradise.Physics;

public enum ColliderType : byte
{
    Sphere = 0,
    Capsule = 1,
    Box = 2,
}

/// <summary>Sphere centered at the collider-local origin.</summary>
public struct SphereGeometry
{
    public float Radius;
}

/// <summary>
/// Capsule aligned to the collider-local Y axis: core segment from (0, -HalfLength, 0) to
/// (0, +HalfLength, 0), inflated by <see cref="Radius"/>. Total height = 2 * (HalfLength + Radius).
/// </summary>
public struct CapsuleGeometry
{
    public float Radius;
    public float HalfLength;
}

/// <summary>Box centered at the collider-local origin.</summary>
public struct BoxGeometry
{
    public Vector3 HalfExtents;
}

/// <summary>
/// Fixed-size tagged union of the primitive collider shapes. Geometry is collider-local and
/// origin-centered; authoring offsets/rotations must be composed into the body transform.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public struct Collider
{
    [FieldOffset(0)] public ColliderType Type;
    [FieldOffset(4)] public CollisionFilter Filter;
    [FieldOffset(16)] public SphereGeometry Sphere;
    [FieldOffset(16)] public CapsuleGeometry Capsule;
    [FieldOffset(16)] public BoxGeometry Box;

    public static Collider CreateSphere(float radius, CollisionFilter filter)
        => new() { Type = ColliderType.Sphere, Filter = filter, Sphere = new SphereGeometry { Radius = radius } };

    public static Collider CreateSphere(float radius) => CreateSphere(radius, CollisionFilter.Default);

    public static Collider CreateCapsule(float radius, float halfLength, CollisionFilter filter)
        => new() { Type = ColliderType.Capsule, Filter = filter, Capsule = new CapsuleGeometry { Radius = radius, HalfLength = halfLength } };

    public static Collider CreateCapsule(float radius, float halfLength) => CreateCapsule(radius, halfLength, CollisionFilter.Default);

    public static Collider CreateBox(Vector3 halfExtents, CollisionFilter filter)
        => new() { Type = ColliderType.Box, Filter = filter, Box = new BoxGeometry { HalfExtents = halfExtents } };

    public static Collider CreateBox(Vector3 halfExtents) => CreateBox(halfExtents, CollisionFilter.Default);

    /// <summary>World-space bounds of this collider at the given pose. No trigonometry —
    /// the box path uses the |R|·h column trick, the capsule path transforms the two cap centers.</summary>
    public readonly Aabb CalculateAabb(in RigidTransform worldFromCollider)
    {
        switch (Type)
        {
            case ColliderType.Sphere:
            {
                var extent = new Vector3(Sphere.Radius);
                return new Aabb(worldFromCollider.Position - extent, worldFromCollider.Position + extent);
            }
            case ColliderType.Capsule:
            {
                Vector3 top = worldFromCollider.Transform(new Vector3(0f, Capsule.HalfLength, 0f));
                Vector3 bottom = worldFromCollider.Transform(new Vector3(0f, -Capsule.HalfLength, 0f));
                var extent = new Vector3(Capsule.Radius);
                return new Aabb(Vector3.Min(top, bottom) - extent, Vector3.Max(top, bottom) + extent);
            }
            case ColliderType.Box:
            default:
            {
                // Extent along each world axis = sum of |rotation columns| scaled by half extents.
                Vector3 cx = Vector3.Abs(worldFromCollider.TransformDirection(new Vector3(Box.HalfExtents.X, 0f, 0f)));
                Vector3 cy = Vector3.Abs(worldFromCollider.TransformDirection(new Vector3(0f, Box.HalfExtents.Y, 0f)));
                Vector3 cz = Vector3.Abs(worldFromCollider.TransformDirection(new Vector3(0f, 0f, Box.HalfExtents.Z)));
                Vector3 extent = cx + cy + cz;
                return new Aabb(worldFromCollider.Position - extent, worldFromCollider.Position + extent);
            }
        }
    }
}
