using System.Numerics;

namespace Paradise.Physics.Test;

public class ColliderCastTests
{
    private static bool Approx(float a, float b, float tolerance = 1e-3f) => MathF.Abs(a - b) <= tolerance;

    private static bool IsUnit(Vector3 v) => MathF.Abs(v.Length() - 1f) <= 1e-3f;

    private static ColliderCastInput Cast(Collider collider, Vector3 start, Vector3 end) => new()
    {
        Collider = collider,
        Orientation = Quaternion.Identity,
        Start = start,
        End = end,
    };

    [Test]
    public async Task sphere_cast_hits_box_at_expected_fraction()
    {
        // Sphere r=0.5 travelling +X over 10m; unit box face at x=-1 → contact when center reaches -1.5.
        var input = Cast(Collider.CreateSphere(0.5f), new(-5f, 0f, 0f), new(5f, 0f, 0f));
        bool hit = ColliderQueries.CastCollider(input, Collider.CreateBox(new Vector3(1f, 1f, 1f)), RigidTransform.Identity, out ColliderCastHit result);

        await Assert.That(hit).IsTrue();
        await Assert.That(Approx(result.Fraction, 0.35f)).IsTrue();
        await Assert.That((result.SurfaceNormal - new Vector3(-1f, 0f, 0f)).Length() <= 1e-2f).IsTrue();
        await Assert.That((result.Position - new Vector3(-1f, 0f, 0f)).Length() <= 1e-2f).IsTrue();
    }

    [Test]
    public async Task upright_capsule_cast_hits_box_face()
    {
        // Capsule r=0.4 → contact when its center reaches x = -1.4.
        var input = Cast(Collider.CreateCapsule(0.4f, 0.5f), new(-5f, 0f, 0f), new(5f, 0f, 0f));
        bool hit = ColliderQueries.CastCollider(input, Collider.CreateBox(new Vector3(1f, 1f, 1f)), RigidTransform.Identity, out ColliderCastHit result);

        await Assert.That(hit).IsTrue();
        await Assert.That(Approx(result.Fraction, 0.36f)).IsTrue();
        await Assert.That((result.SurfaceNormal - new Vector3(-1f, 0f, 0f)).Length() <= 1e-2f).IsTrue();
    }

    [Test]
    public async Task capsule_cast_hits_sphere()
    {
        // Contact when capsule wall (r=0.4) meets sphere (r=0.5): center distance 0.9.
        var input = Cast(Collider.CreateCapsule(0.4f, 0.5f), new(-5f, 0f, 0f), new(5f, 0f, 0f));
        bool hit = ColliderQueries.CastCollider(input, Collider.CreateSphere(0.5f), RigidTransform.Identity, out ColliderCastHit result);

        await Assert.That(hit).IsTrue();
        await Assert.That(Approx(result.Fraction, (5f - 0.9f) / 10f)).IsTrue();
    }

    [Test]
    public async Task cast_starting_in_overlap_reports_fraction_zero_with_unit_normal()
    {
        var input = Cast(Collider.CreateSphere(0.5f), new(1.2f, 0f, 0f), new(5f, 0f, 0f));
        bool hit = ColliderQueries.CastCollider(input, Collider.CreateBox(new Vector3(1f, 1f, 1f)), RigidTransform.Identity, out ColliderCastHit result);

        await Assert.That(hit).IsTrue();
        await Assert.That(result.Fraction).IsEqualTo(0f);
        await Assert.That(IsUnit(result.SurfaceNormal)).IsTrue();
        await Assert.That(float.IsFinite(result.Position.X)).IsTrue();
    }

    [Test]
    public async Task parallel_slide_above_the_surface_misses()
    {
        // Sphere r=0.5 sliding 1mm above the box top (y=1): no approach along the normal.
        var input = Cast(Collider.CreateSphere(0.5f), new(-5f, 1.501f, 0f), new(5f, 1.501f, 0f));
        bool hit = ColliderQueries.CastCollider(input, Collider.CreateBox(new Vector3(1f, 1f, 1f)), RigidTransform.Identity, out _);
        await Assert.That(hit).IsFalse();
    }

    [Test]
    public async Task cast_ending_short_of_contact_misses()
    {
        var input = Cast(Collider.CreateSphere(0.5f), new(-5f, 0f, 0f), new(-3f, 0f, 0f));
        bool hit = ColliderQueries.CastCollider(input, Collider.CreateBox(new Vector3(1f, 1f, 1f)), RigidTransform.Identity, out _);
        await Assert.That(hit).IsFalse();
    }

    [Test]
    public async Task diagonal_cast_onto_box_edge_terminates_with_valid_hit()
    {
        // 45° approach toward the box's +X/+Z edge region — grazing config for CA.
        var input = Cast(Collider.CreateSphere(0.3f), new(4f, 0f, 4f), new(0f, 0f, 0f));
        bool hit = ColliderQueries.CastCollider(input, Collider.CreateBox(new Vector3(1f, 1f, 1f)), RigidTransform.Identity, out ColliderCastHit result);

        await Assert.That(hit).IsTrue();
        await Assert.That(result.Fraction > 0f && result.Fraction < 1f).IsTrue();
        await Assert.That(IsUnit(result.SurfaceNormal)).IsTrue();
    }

    [Test]
    public async Task zero_displacement_cast_hits_only_when_overlapping()
    {
        var overlapping = Cast(Collider.CreateSphere(0.5f), new(1.2f, 0f, 0f), new(1.2f, 0f, 0f));
        var separated = Cast(Collider.CreateSphere(0.5f), new(3f, 0f, 0f), new(3f, 0f, 0f));
        var box = Collider.CreateBox(new Vector3(1f, 1f, 1f));

        bool hitOverlap = ColliderQueries.CastCollider(overlapping, box, RigidTransform.Identity, out ColliderCastHit result);
        bool hitSeparated = ColliderQueries.CastCollider(separated, box, RigidTransform.Identity, out _);

        await Assert.That(hitOverlap).IsTrue();
        await Assert.That(result.Fraction).IsEqualTo(0f);
        await Assert.That(hitSeparated).IsFalse();
    }

    [Test]
    public async Task cast_against_rotated_box_hits_the_rotated_face()
    {
        // Box rotated 45° about Y: its corner edge points toward -X at sqrt(2).
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f);
        var input = Cast(Collider.CreateSphere(0.5f), new(-5f, 0f, 0f), new(5f, 0f, 0f));
        bool hit = ColliderQueries.CastCollider(input, Collider.CreateBox(new Vector3(1f, 1f, 1f)), new RigidTransform(Vector3.Zero, rotation), out ColliderCastHit result);

        float expectedContactX = -(MathF.Sqrt(2f) + 0.5f);
        await Assert.That(hit).IsTrue();
        await Assert.That(Approx(result.Fraction, (expectedContactX + 5f) / 10f, 5e-3f)).IsTrue();
    }
}
