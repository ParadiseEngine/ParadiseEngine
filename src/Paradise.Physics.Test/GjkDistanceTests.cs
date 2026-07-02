using System.Numerics;

namespace Paradise.Physics.Test;

public class GjkDistanceTests
{
    private const float Tolerance = 1e-4f;

    private static bool Approx(float a, float b, float tolerance = Tolerance) => MathF.Abs(a - b) <= tolerance;

    private static bool IsUnit(Vector3 v) => MathF.Abs(v.Length() - 1f) <= 1e-3f;

    private static Quaternion RandomRotation(Random random)
    {
        var axis = Vector3.Normalize(new Vector3(
            (float)(random.NextDouble() * 2.0 - 1.0),
            (float)(random.NextDouble() * 2.0 - 1.0),
            (float)(random.NextDouble() * 2.0 - 1.0) + 1e-3f));
        return Quaternion.CreateFromAxisAngle(axis, (float)(random.NextDouble() * Math.PI * 2.0));
    }

    private static Vector3 RandomPosition(Random random, float range = 5f) => new(
        (float)(random.NextDouble() * 2.0 - 1.0) * range,
        (float)(random.NextDouble() * 2.0 - 1.0) * range,
        (float)(random.NextDouble() * 2.0 - 1.0) * range);

    private static (Vector3 P0, Vector3 P1) CapsuleSegment(in RigidTransform transform, float halfLength)
    {
        Vector3 halfAxis = transform.TransformDirection(new Vector3(0f, halfLength, 0f));
        return (transform.Position - halfAxis, transform.Position + halfAxis);
    }

    [Test]
    public async Task sphere_sphere_distance_is_exact()
    {
        var a = Collider.CreateSphere(1f);
        var b = Collider.CreateSphere(0.5f);
        var tb = new RigidTransform(new Vector3(5f, 0f, 0f), Quaternion.Identity);

        ColliderQueries.DistanceBetween(a, RigidTransform.Identity, b, tb, out DistanceHit result);

        await Assert.That(Approx(result.Distance, 3.5f)).IsTrue();
        await Assert.That((result.SurfaceNormal - new Vector3(-1f, 0f, 0f)).Length() <= 1e-3f).IsTrue();
        await Assert.That((result.Position - new Vector3(4.5f, 0f, 0f)).Length() <= 1e-3f).IsTrue();
    }

    [Test]
    public async Task sphere_box_distance_matches_clamped_point()
    {
        var sphere = Collider.CreateSphere(0.5f);
        var box = Collider.CreateBox(new Vector3(1f, 1f, 1f));
        var ta = new RigidTransform(new Vector3(3f, 0.2f, -0.4f), Quaternion.Identity);

        ColliderQueries.DistanceBetween(sphere, ta, box, RigidTransform.Identity, out DistanceHit result);

        await Assert.That(Approx(result.Distance, 1.5f, 1e-3f)).IsTrue();
    }

    [Test]
    public async Task shallow_sphere_box_penetration_is_exact_while_cores_are_separate()
    {
        // Sphere center outside the box, surfaces overlapping by 0.2.
        var sphere = Collider.CreateSphere(0.5f);
        var box = Collider.CreateBox(new Vector3(1f, 1f, 1f));
        var ta = new RigidTransform(new Vector3(1.3f, 0f, 0f), Quaternion.Identity);

        ColliderQueries.DistanceBetween(sphere, ta, box, RigidTransform.Identity, out DistanceHit result);

        await Assert.That(Approx(result.Distance, -0.2f, 1e-3f)).IsTrue();
        await Assert.That((result.SurfaceNormal - new Vector3(1f, 0f, 0f)).Length() <= 1e-3f).IsTrue();
    }

    [Test]
    public async Task capsule_box_face_distance_is_exact()
    {
        var capsule = Collider.CreateCapsule(0.4f, 0.5f);
        var box = Collider.CreateBox(new Vector3(1f, 1f, 1f));
        var ta = new RigidTransform(new Vector3(3f, 0f, 0f), Quaternion.Identity);

        ColliderQueries.DistanceBetween(capsule, ta, box, RigidTransform.Identity, out DistanceHit result);

        await Assert.That(Approx(result.Distance, 1.6f, 1e-3f)).IsTrue();
        await Assert.That((result.SurfaceNormal - new Vector3(1f, 0f, 0f)).Length() <= 1e-3f).IsTrue();
    }

    [Test]
    public async Task deep_penetration_fallback_returns_finite_unit_normal()
    {
        // Sphere center INSIDE the box: core point inside → analytic fallback path.
        var sphere = Collider.CreateSphere(0.5f);
        var box = Collider.CreateBox(new Vector3(1f, 1f, 1f));
        var ta = new RigidTransform(new Vector3(0.6f, 0.1f, -0.2f), Quaternion.Identity);

        ColliderQueries.DistanceBetween(sphere, ta, box, RigidTransform.Identity, out DistanceHit result);

        await Assert.That(result.Distance < 0f).IsTrue();
        await Assert.That(float.IsFinite(result.Distance)).IsTrue();
        await Assert.That(IsUnit(result.SurfaceNormal)).IsTrue();
        // Nearest face of (0.6, 0.1, -0.2) in a unit box is +X.
        await Assert.That((result.SurfaceNormal - new Vector3(1f, 0f, 0f)).Length() <= 1e-3f).IsTrue();
    }

    [Test]
    public async Task coincident_spheres_produce_unit_fallback_normal()
    {
        var a = Collider.CreateSphere(1f);
        var b = Collider.CreateSphere(1f);

        ColliderQueries.DistanceBetween(a, RigidTransform.Identity, b, RigidTransform.Identity, out DistanceHit result);

        await Assert.That(result.Distance < 0f).IsTrue();
        await Assert.That(IsUnit(result.SurfaceNormal)).IsTrue();
    }

    [Test]
    public async Task differential_sphere_sphere_battery_matches_closed_form()
    {
        var random = new Random(12345);
        for (int i = 0; i < 1000; i++)
        {
            float ra = 0.1f + (float)random.NextDouble() * 1.4f;
            float rb = 0.1f + (float)random.NextDouble() * 1.4f;
            var ta = new RigidTransform(RandomPosition(random), RandomRotation(random));
            var tb = new RigidTransform(RandomPosition(random), RandomRotation(random));

            ColliderQueries.DistanceBetween(Collider.CreateSphere(ra), ta, Collider.CreateSphere(rb), tb, out DistanceHit result);
            float expected = Vector3.Distance(ta.Position, tb.Position) - ra - rb;

            if (Vector3.Distance(ta.Position, tb.Position) < 1e-3f) continue; // coincident-center fallback case
            await Assert.That(Approx(result.Distance, expected, 1e-3f)).IsTrue();
        }
    }

    [Test]
    public async Task differential_sphere_capsule_battery_matches_closed_form()
    {
        var random = new Random(23456);
        for (int i = 0; i < 1000; i++)
        {
            float ra = 0.1f + (float)random.NextDouble() * 1.4f;
            float rb = 0.1f + (float)random.NextDouble() * 1.4f;
            float halfLength = 0.1f + (float)random.NextDouble() * 1.9f;
            var ta = new RigidTransform(RandomPosition(random), RandomRotation(random));
            var tb = new RigidTransform(RandomPosition(random), RandomRotation(random));

            ColliderQueries.DistanceBetween(Collider.CreateSphere(ra), ta, Collider.CreateCapsule(rb, halfLength), tb, out DistanceHit result);

            (Vector3 p0, Vector3 p1) = CapsuleSegment(tb, halfLength);
            float coreDistance = MathF.Sqrt(ClosestPoints.PointSegmentDistanceSquared(ta.Position, p0, p1));
            if (coreDistance < 1e-3f) continue; // core-overlap fallback case
            float expected = coreDistance - ra - rb;

            await Assert.That(Approx(result.Distance, expected, 1e-3f)).IsTrue();
        }
    }

    [Test]
    public async Task differential_capsule_capsule_battery_matches_closed_form()
    {
        var random = new Random(34567);
        for (int i = 0; i < 1000; i++)
        {
            float ra = 0.1f + (float)random.NextDouble() * 1.4f;
            float rb = 0.1f + (float)random.NextDouble() * 1.4f;
            float halfA = 0.1f + (float)random.NextDouble() * 1.9f;
            float halfB = 0.1f + (float)random.NextDouble() * 1.9f;
            var ta = new RigidTransform(RandomPosition(random), RandomRotation(random));
            var tb = new RigidTransform(RandomPosition(random), RandomRotation(random));

            ColliderQueries.DistanceBetween(Collider.CreateCapsule(ra, halfA), ta, Collider.CreateCapsule(rb, halfB), tb, out DistanceHit result);

            (Vector3 a0, Vector3 a1) = CapsuleSegment(ta, halfA);
            (Vector3 b0, Vector3 b1) = CapsuleSegment(tb, halfB);
            ClosestPoints.SegmentSegment(a0, a1, b0, b1, out Vector3 ca, out Vector3 cb);
            float coreDistance = Vector3.Distance(ca, cb);
            if (coreDistance < 1e-3f) continue; // core-overlap fallback case
            float expected = coreDistance - ra - rb;

            await Assert.That(Approx(result.Distance, expected, 1e-3f)).IsTrue();
        }
    }

    [Test]
    public async Task differential_sphere_vs_axis_aligned_box_battery_matches_clamp()
    {
        var random = new Random(45678);
        var box = Collider.CreateBox(new Vector3(1f, 0.8f, 1.2f));
        for (int i = 0; i < 1000; i++)
        {
            float radius = 0.1f + (float)random.NextDouble() * 0.9f;
            Vector3 center = RandomPosition(random, 4f);
            Vector3 clamped = Vector3.Clamp(center, -box.Box.HalfExtents, box.Box.HalfExtents);
            float coreDistance = Vector3.Distance(center, clamped);
            if (coreDistance < 1e-2f) continue; // center inside/on the box → fallback path

            var ta = new RigidTransform(center, Quaternion.Identity);
            ColliderQueries.DistanceBetween(Collider.CreateSphere(radius), ta, box, RigidTransform.Identity, out DistanceHit result);

            await Assert.That(Approx(result.Distance, coreDistance - radius, 1e-3f)).IsTrue();
        }
    }
}
