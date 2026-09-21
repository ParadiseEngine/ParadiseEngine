using System.Numerics;
using Paradise.Export.Geometry;

namespace Paradise.Export.Tests;

// Mirrors ParadiseUnityEditor's ColliderExportUtilityTests expected values so the Godot
// scale-folding stays identical to the Unity tool's.
public class ColliderScaleFoldTests
{
    private static readonly Vector3 UnitScale = Vector3.One;

    [Test]
    public async Task box_size_at_unit_scale_is_unchanged()
    {
        // Unity test: size (2,4,6) at unit scale exports (2,4,6).
        Vector3 size = ColliderScaleFold.BoxSize(new Vector3(2f, 4f, 6f), UnitScale);
        await Assert.That(size).IsEqualTo(new Vector3(2f, 4f, 6f));
    }

    [Test]
    public async Task box_size_folds_absolute_scale_per_axis()
    {
        Vector3 size = ColliderScaleFold.BoxSize(new Vector3(2f, 4f, 6f), new Vector3(2f, -1f, 0.5f));
        await Assert.That(size).IsEqualTo(new Vector3(4f, 4f, 3f));
    }

    [Test]
    public async Task sphere_radius_at_unit_scale_is_unchanged()
    {
        // Unity test: radius 3 at unit scale exports 3.
        await Assert.That(ColliderScaleFold.SphereRadius(3f, UnitScale)).IsEqualTo(3f);
    }

    [Test]
    public async Task sphere_radius_folds_largest_absolute_axis()
    {
        await Assert.That(ColliderScaleFold.SphereRadius(2f, new Vector3(1f, -3f, 2f))).IsEqualTo(6f);
    }

    [Test]
    public async Task capsule_at_unit_scale_is_unchanged()
    {
        // Unity test: radius 0.5, height 2 at unit scale exports 0.5 / 2.
        await Assert.That(ColliderScaleFold.CapsuleRadius(0.5f, UnitScale)).IsEqualTo(0.5f);
        await Assert.That(ColliderScaleFold.CapsuleHeight(2f, UnitScale)).IsEqualTo(2f);
    }

    [Test]
    public async Task capsule_radius_uses_max_xz_height_uses_y()
    {
        var scale = new Vector3(3f, 5f, -2f);
        await Assert.That(ColliderScaleFold.CapsuleRadius(1f, scale)).IsEqualTo(3f); // max(|3|,|-2|)
        await Assert.That(ColliderScaleFold.CapsuleHeight(1f, scale)).IsEqualTo(5f); // |5|
    }

    [Test]
    public async Task relative_scale_divides_and_guards_zero()
    {
        Vector3 rel = ColliderScaleFold.RelativeScale(new Vector3(4f, 6f, 9f), new Vector3(2f, 3f, 0f));
        await Assert.That(rel).IsEqualTo(new Vector3(2f, 2f, 0f)); // z divisor 0 → guarded to 0
    }
}
