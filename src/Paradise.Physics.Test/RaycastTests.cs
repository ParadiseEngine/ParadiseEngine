using System.Numerics;

namespace Paradise.Physics.Test;

public class RaycastTests
{
    private static bool Approx(float a, float b, float tolerance = 1e-4f) => MathF.Abs(a - b) <= tolerance;

    private static bool Approx(Vector3 a, Vector3 b, float tolerance = 1e-4f) => (a - b).Length() <= tolerance;

    private static RaycastInput Ray(Vector3 start, Vector3 end) => new() { Start = start, End = end, Filter = CollisionFilter.Default };

    [Test]
    public async Task sphere_center_hit_reports_exact_fraction_position_and_normal()
    {
        var sphere = Collider.CreateSphere(1f);
        bool hit = ColliderQueries.RayCollider(Ray(new(-5f, 0f, 0f), new(5f, 0f, 0f)), sphere, RigidTransform.Identity, out RaycastHit result);

        await Assert.That(hit).IsTrue();
        await Assert.That(Approx(result.Fraction, 0.4f)).IsTrue();
        await Assert.That(Approx(result.Position, new Vector3(-1f, 0f, 0f))).IsTrue();
        await Assert.That(Approx(result.SurfaceNormal, new Vector3(-1f, 0f, 0f))).IsTrue();
    }

    [Test]
    public async Task box_face_hit_reports_entering_face_normal()
    {
        var box = Collider.CreateBox(new Vector3(1f, 1f, 1f));
        bool hit = ColliderQueries.RayCollider(Ray(new(-5f, 0.5f, 0.5f), new(5f, 0.5f, 0.5f)), box, RigidTransform.Identity, out RaycastHit result);

        await Assert.That(hit).IsTrue();
        await Assert.That(Approx(result.Fraction, 0.4f)).IsTrue();
        await Assert.That(Approx(result.SurfaceNormal, new Vector3(-1f, 0f, 0f))).IsTrue();
    }

    [Test]
    public async Task translated_box_hit_uses_world_space()
    {
        var box = Collider.CreateBox(new Vector3(1f, 1f, 1f));
        var transform = new RigidTransform(new Vector3(10f, 0f, 0f), Quaternion.Identity);
        bool hit = ColliderQueries.RayCollider(Ray(Vector3.Zero, new(20f, 0f, 0f)), box, transform, out RaycastHit result);

        await Assert.That(hit).IsTrue();
        await Assert.That(Approx(result.Fraction, 0.45f)).IsTrue();
        await Assert.That(Approx(result.Position, new Vector3(9f, 0f, 0f))).IsTrue();
    }

    [Test]
    public async Task rotated_box_corner_faces_the_ray()
    {
        // Unit box rotated 45° about Y presents a corner edge at x = -sqrt(2) toward the ray.
        var box = Collider.CreateBox(new Vector3(1f, 1f, 1f));
        var transform = new RigidTransform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f));
        bool hit = ColliderQueries.RayCollider(Ray(new(-5f, 0f, 0f), new(0f, 0f, 0f)), box, transform, out RaycastHit result);

        await Assert.That(hit).IsTrue();
        await Assert.That(Approx(result.Fraction, (5f - MathF.Sqrt(2f)) / 5f, 1e-3f)).IsTrue();
    }

    [Test]
    public async Task capsule_wall_hit_has_horizontal_normal()
    {
        var capsule = Collider.CreateCapsule(0.4f, 0.5f);
        bool hit = ColliderQueries.RayCollider(Ray(new(-5f, 0.2f, 0f), new(5f, 0.2f, 0f)), capsule, RigidTransform.Identity, out RaycastHit result);

        await Assert.That(hit).IsTrue();
        await Assert.That(Approx(result.Fraction, 4.6f / 10f)).IsTrue();
        await Assert.That(Approx(result.SurfaceNormal, new Vector3(-1f, 0f, 0f))).IsTrue();
    }

    [Test]
    public async Task capsule_cap_hit_from_above()
    {
        var capsule = Collider.CreateCapsule(0.4f, 0.5f);
        bool hit = ColliderQueries.RayCollider(Ray(new(0f, 5f, 0f), new(0f, -5f, 0f)), capsule, RigidTransform.Identity, out RaycastHit result);

        await Assert.That(hit).IsTrue();
        await Assert.That(Approx(result.Fraction, (5f - 0.9f) / 10f)).IsTrue();
        await Assert.That(Approx(result.SurfaceNormal, new Vector3(0f, 1f, 0f))).IsTrue();
    }

    [Test]
    public async Task capsule_axis_parallel_offset_ray_hits_cap_sphere()
    {
        var capsule = Collider.CreateCapsule(0.4f, 0.5f);
        bool hit = ColliderQueries.RayCollider(Ray(new(0.2f, 5f, 0f), new(0.2f, -5f, 0f)), capsule, RigidTransform.Identity, out RaycastHit result);

        float expectedY = 0.5f + MathF.Sqrt(0.4f * 0.4f - 0.2f * 0.2f);
        await Assert.That(hit).IsTrue();
        await Assert.That(Approx(result.Fraction, (5f - expectedY) / 10f, 1e-3f)).IsTrue();
        await Assert.That(result.SurfaceNormal.Y > 0f).IsTrue();
    }

    [Test]
    public async Task ray_starting_inside_hits_at_fraction_zero_with_backward_normal()
    {
        var box = Collider.CreateBox(new Vector3(1f, 1f, 1f));
        bool hit = ColliderQueries.RayCollider(Ray(new(0.5f, 0f, 0f), new(5f, 0f, 0f)), box, RigidTransform.Identity, out RaycastHit result);

        await Assert.That(hit).IsTrue();
        await Assert.That(result.Fraction).IsEqualTo(0f);
        await Assert.That(Approx(result.Position, new Vector3(0.5f, 0f, 0f))).IsTrue();
        await Assert.That(Approx(result.SurfaceNormal, new Vector3(-1f, 0f, 0f))).IsTrue();
    }

    [Test]
    public async Task ray_pointing_away_misses()
    {
        var sphere = Collider.CreateSphere(1f);
        bool hit = ColliderQueries.RayCollider(Ray(new(-5f, 0f, 0f), new(-10f, 0f, 0f)), sphere, RigidTransform.Identity, out _);
        await Assert.That(hit).IsFalse();
    }

    [Test]
    public async Task grazing_ray_above_box_misses()
    {
        var box = Collider.CreateBox(new Vector3(1f, 1f, 1f));
        bool hit = ColliderQueries.RayCollider(Ray(new(-5f, 1.5f, 0f), new(5f, 1.5f, 0f)), box, RigidTransform.Identity, out _);
        await Assert.That(hit).IsFalse();
    }

    [Test]
    public async Task zero_length_ray_outside_misses_and_inside_hits()
    {
        var sphere = Collider.CreateSphere(1f);
        bool missed = ColliderQueries.RayCollider(Ray(new(5f, 0f, 0f), new(5f, 0f, 0f)), sphere, RigidTransform.Identity, out _);
        bool inside = ColliderQueries.RayCollider(Ray(Vector3.Zero, Vector3.Zero), sphere, RigidTransform.Identity, out RaycastHit result);

        await Assert.That(missed).IsFalse();
        await Assert.That(inside).IsTrue();
        await Assert.That(result.Fraction).IsEqualTo(0f);
    }
}
