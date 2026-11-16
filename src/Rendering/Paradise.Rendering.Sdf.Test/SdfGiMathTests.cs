namespace Paradise.Rendering.Sdf.Test;

/// <summary>The probe grid and ray set the GI shaders compute, checked on the CPU; needs no GPU.</summary>
public class SdfGiMathTests
{
    [Test]
    public async Task the_grid_covers_its_bounds_at_the_requested_spacing()
    {
        var grid = SdfGiMath.Fit(new SdfGi { BoundsMin = new Vector3(-3f, -0.5f, -2f), BoundsMax = new Vector3(3f, 3.5f, 2f), ProbeSpacing = 1f });

        await Assert.That((grid.CountX, grid.CountY, grid.CountZ)).IsEqualTo((7, 5, 5));
        await Assert.That(grid.Spacing).IsEqualTo(1f);
        await Assert.That(Vector3.Distance(grid.Origin, new Vector3(-3f, -0.5f, -2f))).IsLessThan(1e-5f);
        await Assert.That(Vector3.Distance(grid.Position(grid.Count - 1), new Vector3(3f, 3.5f, 2f))).IsLessThan(1e-5f);
        // Numbered a horizontal layer at a time, as the shaders decode a row of the ray texture.
        await Assert.That(grid.Index(2, 3, 4)).IsEqualTo(2 + 7 * (4 + 5 * 3));
        await Assert.That(Vector3.Distance(grid.Position(grid.Index(2, 3, 4)), new Vector3(-1f, 2.5f, 2f))).IsLessThan(1e-5f);
        await Assert.That(grid.AtlasWidth).IsEqualTo((uint)(64 * SdfGiMath.TexelsPerProbe));
        await Assert.That(grid.AtlasHeight).IsEqualTo(3u); // 175 probes, 64 to a row
    }

    [Test]
    public async Task the_grid_widens_its_spacing_to_stay_within_the_probe_budget()
    {
        var settings = new SdfGi { BoundsMin = new Vector3(-50f), BoundsMax = new Vector3(50f), ProbeSpacing = 0.5f, MaxProbes = 1000 };
        var grid = SdfGiMath.Fit(settings);

        await Assert.That(grid.Count).IsLessThanOrEqualTo(1000);
        await Assert.That(grid.Count).IsGreaterThan(500);
        await Assert.That(grid.Spacing).IsGreaterThan(0.5f);
        // Still centered on the bounds and still covering them.
        var last = grid.Position(grid.Count - 1);
        await Assert.That(Vector3.Distance((grid.Origin + last) * 0.5f, Vector3.Zero)).IsLessThan(1e-3f);
        await Assert.That(MathF.Min(last.X, MathF.Min(last.Y, last.Z))).IsGreaterThanOrEqualTo(50f - 1e-3f);

        // Whatever is asked, the count never exceeds what the ray texture has rows for.
        var huge = SdfGiMath.Fit(settings with { MaxProbes = int.MaxValue, ProbeSpacing = 0.01f });
        await Assert.That(huge.Count).IsLessThanOrEqualTo(SdfGiMath.MaxProbes);
    }

    [Test]
    public async Task degenerate_bounds_still_fit_a_cell_of_probes()
    {
        var flat = SdfGiMath.Fit(new SdfGi { BoundsMin = new Vector3(1f, 2f, 3f), BoundsMax = new Vector3(1f, 2f, 3f) });
        await Assert.That((flat.CountX, flat.CountY, flat.CountZ)).IsEqualTo((2, 2, 2));

        var broken = SdfGiMath.Fit(new SdfGi { BoundsMin = new Vector3(float.NaN), BoundsMax = new Vector3(float.PositiveInfinity), ProbeSpacing = float.NaN });
        await Assert.That(broken.Count).IsGreaterThanOrEqualTo(8);
        await Assert.That(float.IsFinite(broken.Origin.LengthSquared()) && float.IsFinite(broken.Spacing)).IsTrue();
    }

    [Test]
    [Arguments(16)]
    [Arguments(32)]
    [Arguments(64)]
    public async Task the_ray_set_has_the_moments_the_probe_reconstruction_assumes(int count)
    {
        // A probe stores the mean of radiance and three times the mean of radiance x direction,
        // and reads radiance back as m + v . w. That is exact for light linear in direction only
        // if the directions average to zero and their outer products to a third of the identity.
        var rotation = SdfGiMath.FrameRotation(7);
        var mean = Vector3.Zero;
        var xx = 0f;
        var yy = 0f;
        var zz = 0f;
        var xy = 0f;
        var yz = 0f;
        var zx = 0f;
        for (var ray = 0; ray < count; ray++)
        {
            var direction = SdfGiMath.RayDirection(ray, count, rotation);
            await Assert.That(MathF.Abs(direction.Length() - 1f)).IsLessThan(1e-5f);
            mean += direction / count;
            xx += direction.X * direction.X / count;
            yy += direction.Y * direction.Y / count;
            zz += direction.Z * direction.Z / count;
            xy += direction.X * direction.Y / count;
            yz += direction.Y * direction.Z / count;
            zx += direction.Z * direction.X / count;
        }

        var tolerance = 1.5f / count;
        await Assert.That(mean.Length()).IsLessThan(tolerance);
        await Assert.That(MathF.Abs(xx - 1f / 3f)).IsLessThan(tolerance);
        await Assert.That(MathF.Abs(yy - 1f / 3f)).IsLessThan(tolerance);
        await Assert.That(MathF.Abs(zz - 1f / 3f)).IsLessThan(tolerance);
        await Assert.That(MathF.Max(MathF.Abs(xy), MathF.Max(MathF.Abs(yz), MathF.Abs(zx)))).IsLessThan(tolerance);
    }

    [Test]
    public async Task frame_rotations_are_rigid_and_differ_from_frame_to_frame()
    {
        var previous = SdfGiMath.FrameRotation(0);
        for (var frame = 1; frame < 16; frame++)
        {
            var rotation = SdfGiMath.FrameRotation(frame);
            Matrix4x4.Invert(rotation, out var inverse);
            await Assert.That(MathF.Abs(rotation.GetDeterminant() - 1f)).IsLessThan(1e-4f);
            await Assert.That(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX, inverse), Vector3.TransformNormal(Vector3.UnitX, Matrix4x4.Transpose(rotation)))).IsLessThan(1e-4f);
            // Each frame's rays fall somewhere the last frame's did not.
            await Assert.That(Vector3.Distance(SdfGiMath.RayDirection(0, 32, rotation), SdfGiMath.RayDirection(0, 32, previous))).IsGreaterThan(0.05f);
            previous = rotation;
        }
    }

    [Test]
    public async Task fog_opacity_follows_beers_law_and_thins_with_height()
    {
        var fog = new SdfFog { Enabled = true, Density = 0.2f };
        await Assert.That(MathF.Abs(SdfMath.FogOpacity(fog, Vector3.Zero, Vector3.UnitZ, 4f) - (1f - MathF.Exp(-0.8f)))).IsLessThan(1e-6f);
        await Assert.That(SdfMath.FogOpacity(fog with { Enabled = false }, Vector3.Zero, Vector3.UnitZ, 4f)).IsEqualTo(0f);
        await Assert.That(SdfMath.FogOpacity(fog with { StartDistance = 5f }, Vector3.Zero, Vector3.UnitZ, 4f)).IsEqualTo(0f);
        await Assert.That(SdfMath.FogOpacity(fog with { MaxOpacity = 0.25f }, Vector3.Zero, Vector3.UnitZ, 400f)).IsEqualTo(0.25f);

        // Straight up through fog whose density is 0.2 exp(-y): the optical depth to height h is
        // 0.2 (1 - exp(-h)), and never more than 0.2 however far the ray goes.
        var layered = fog with { HeightFalloff = 1f };
        await Assert.That(MathF.Abs(SdfMath.FogOpacity(layered, Vector3.Zero, Vector3.UnitY, 2f) - (1f - MathF.Exp(-0.2f * (1f - MathF.Exp(-2f)))))).IsLessThan(1e-6f);
        await Assert.That(SdfMath.FogOpacity(layered, Vector3.Zero, Vector3.UnitY, 1000f)).IsLessThan(1f - MathF.Exp(-0.2f) + 1e-6f);
        // A level ray stays at one height, so it sees that height's density all the way.
        await Assert.That(MathF.Abs(SdfMath.FogOpacity(layered, new Vector3(0f, 1f, 0f), Vector3.UnitX, 3f) - (1f - MathF.Exp(-0.6f * MathF.Exp(-1f))))).IsLessThan(1e-6f);
        // Splitting a ray in two multiplies the transmittances.
        var direction = Vector3.Normalize(new Vector3(1f, -0.4f, 0.3f));
        var eye = new Vector3(0f, 3f, 0f);
        var whole = 1f - SdfMath.FogOpacity(layered, eye, direction, 5f);
        var first = 1f - SdfMath.FogOpacity(layered, eye, direction, 2f);
        var second = 1f - SdfMath.FogOpacity(layered, eye + direction * 2f, direction, 3f);
        await Assert.That(MathF.Abs(whole - first * second)).IsLessThan(1e-5f);
    }
}
