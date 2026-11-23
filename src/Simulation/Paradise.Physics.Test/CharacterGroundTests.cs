using System.Numerics;

namespace Paradise.Physics.Test;

public class CharacterGroundTests
{
    private const float CapsuleRadius = 0.3f;
    private const float CapsuleHalfLength = 0.6f;
    private const float FeetOffset = CapsuleRadius + CapsuleHalfLength;

    private static readonly GroundSettings s_settings = new(snapDistance: 0.3f, stepHeight: 0.35f, maxSlopeDegrees: 45f);

    private static bool Approx(float a, float b, float tolerance = 1e-3f) => MathF.Abs(a - b) <= tolerance;

    private static CollisionWorld Build(params (Collider Collider, RigidTransform Transform)[] bodies)
    {
        var colliders = new Collider[bodies.Length];
        var transforms = new RigidTransform[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            colliders[i] = bodies[i].Collider;
            transforms[i] = bodies[i].Transform;
        }

        return CollisionWorld.Build(colliders, transforms);
    }

    // A box whose top face is y = top, covering x ∈ [minX, maxX], z ∈ [0, 20].
    private static (Collider, RigidTransform) Slab(float minX, float maxX, float top, CollisionFilter filter)
        => (Collider.CreateBox(new Vector3((maxX - minX) * 0.5f, 0.5f, 10f), filter),
            new RigidTransform(new Vector3((minX + maxX) * 0.5f, top - 0.5f, 10f), Quaternion.Identity));

    private static (Collider, RigidTransform) Ground(float minX = 0f, float maxX = 20f, float top = 0f)
        => Slab(minX, maxX, top, CollisionFilter.Ground);

    // A 20 m slab centred on (10, 0, 10), rising towards +X at the given angle.
    private static (Collider, RigidTransform) Slope(float degrees)
        => (Collider.CreateBox(new Vector3(10f, 0.5f, 10f), CollisionFilter.Ground),
            new RigidTransform(new Vector3(10f, 0f, 10f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * (MathF.PI / 180f))));

    private static float SlopeHeight(float degrees, float x)
    {
        float radians = degrees * (MathF.PI / 180f);
        return (0.5f + MathF.Sin(radians) * (x - 10f)) / MathF.Cos(radians);
    }

    [Test]
    public async Task flat_ground_reports_contact_normal_and_body()
    {
        using CollisionWorld world = Build(Slab(-5f, -1f, 3f, CollisionFilter.Ground), Ground());
        bool grounded = CharacterGround.Probe(world, s_settings, new Vector3(5f, 0.1f, 5f), out GroundHit ground);

        await Assert.That(grounded).IsTrue();
        await Assert.That(ground.BodyIndex).IsEqualTo(1);
        await Assert.That(Approx(ground.Position.Y, 0f)).IsTrue();
        await Assert.That(Approx(ground.Position.X, 5f) && Approx(ground.Position.Z, 5f)).IsTrue();
        await Assert.That(Approx(ground.SurfaceNormal.Y, 1f)).IsTrue();
        await Assert.That(Approx(ground.Distance, 0.1f)).IsTrue();
    }

    [Test]
    public async Task feet_slightly_below_the_surface_find_it_within_the_step_height()
    {
        using CollisionWorld world = Build(Ground());
        bool grounded = CharacterGround.SnapToGround(world, s_settings, new Vector3(5f, -0.2f, 5f), out Vector3 feet, out GroundHit ground);

        await Assert.That(grounded).IsTrue();
        await Assert.That(Approx(feet.Y, 0f)).IsTrue();
        await Assert.That(Approx(ground.Distance, -0.2f)).IsTrue();
    }

    [Test]
    [Arguments(30f, 30f, true)]
    [Arguments(45f, 45f, true)]
    [Arguments(46f, 45f, false)]
    [Arguments(60f, 45f, false)]
    public async Task slopes_are_walkable_up_to_the_limit(float slopeDegrees, float limitDegrees, bool walkable)
    {
        using CollisionWorld world = Build(Slope(slopeDegrees));
        var settings = new GroundSettings(snapDistance: 1f, stepHeight: 1f, maxSlopeDegrees: limitDegrees);
        var feet = new Vector3(10f, SlopeHeight(slopeDegrees, 10f), 10f);
        bool grounded = CharacterGround.Probe(world, settings, feet, out GroundHit ground);

        await Assert.That(grounded).IsEqualTo(walkable);
        await Assert.That(ground.BodyIndex).IsEqualTo(0); // a steep surface is still reported
        await Assert.That(Approx(ground.SurfaceNormal.Y, MathF.Cos(slopeDegrees * (MathF.PI / 180f)))).IsTrue();
    }

    [Test]
    public async Task a_probe_starting_inside_a_body_is_not_ground()
    {
        using CollisionWorld world = Build(Slope(60f));
        var settings = new GroundSettings(snapDistance: 1f, stepHeight: 0.35f, maxSlopeDegrees: 45f);
        var feet = new Vector3(10f, SlopeHeight(60f, 10f) - 1f, 10f);
        bool grounded = CharacterGround.SnapToGround(world, settings, feet, out Vector3 snapped, out GroundHit ground);

        await Assert.That(grounded).IsFalse();
        await Assert.That(ground.BodyIndex).IsEqualTo(0);
        await Assert.That(snapped).IsEqualTo(feet);
    }

    [Test]
    public async Task settings_need_a_positive_step_height()
    {
        await Assert.That(() => new GroundSettings(snapDistance: 0.3f, stepHeight: 0f, maxSlopeDegrees: 45f))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(0.29f, true)]
    [Arguments(0.31f, false)]
    public async Task snap_reaches_exactly_the_snap_distance(float hover, bool grounded)
    {
        using CollisionWorld world = Build(Ground());
        var feet = new Vector3(5f, hover, 5f);
        bool snapped = CharacterGround.SnapToGround(world, s_settings, feet, out Vector3 result, out GroundHit ground);

        await Assert.That(snapped).IsEqualTo(grounded);
        await Assert.That(result.Y).IsEqualTo(grounded ? ground.Position.Y : hover);
        await Assert.That(ground.BodyIndex).IsEqualTo(grounded ? 0 : -1);
    }

    [Test]
    public async Task bodies_outside_the_ground_layer_are_not_support()
    {
        var prop = new CollisionFilter { BelongsTo = ~CollisionFilter.GroundLayer, CollidesWith = ~0u };
        using CollisionWorld world = Build(Slab(4f, 6f, 0.2f, prop), Ground());
        var feet = new Vector3(5f, 0.2f, 5f);

        bool grounded = CharacterGround.Probe(world, s_settings, feet, out GroundHit ground);
        await Assert.That(grounded).IsTrue();
        await Assert.That(ground.BodyIndex).IsEqualTo(1);
        await Assert.That(Approx(ground.Position.Y, 0f)).IsTrue();

        // Without the ground convention the prop's top is the closest support.
        GroundSettings anything = s_settings with { Filter = CollisionFilter.Default };
        CharacterGround.Probe(world, anything, feet, out GroundHit any);
        await Assert.That(any.BodyIndex).IsEqualTo(0);
    }

    [Test]
    public async Task bodies_with_the_default_filter_count_as_ground()
    {
        using CollisionWorld world = Build(Slab(0f, 20f, 0f, CollisionFilter.Default));
        bool grounded = CharacterGround.Probe(world, s_settings, new Vector3(5f, 0f, 5f), out _);
        await Assert.That(grounded).IsTrue();
    }

    [Test]
    public async Task ledge_reports_the_edge_point_and_supported_fraction()
    {
        using CollisionWorld world = Build(Ground());
        CharacterGround.Probe(world, s_settings, new Vector3(19f, 0f, 5f), out GroundHit start);
        bool lost = CharacterGround.FindLedge(world, s_settings, start, new Vector3(2f, 0f, 0f), out GroundLedge ledge);

        await Assert.That(lost).IsTrue();
        await Assert.That(Approx(ledge.SupportedFraction, 0.5f)).IsTrue();
        await Assert.That(Approx(ledge.Edge.Position.X, 20f)).IsTrue();
        await Assert.That(ledge.Edge.Position.X <= 20f).IsTrue();
        await Assert.That(ledge.Edge.BodyIndex).IsEqualTo(0);
    }

    [Test]
    public async Task ledge_holds_while_support_continues()
    {
        using CollisionWorld world = Build(Ground());
        CharacterGround.Probe(world, s_settings, new Vector3(5f, 0f, 5f), out GroundHit start);
        bool lost = CharacterGround.FindLedge(world, s_settings, start, new Vector3(3f, 7f, -2f), out GroundLedge ledge);

        await Assert.That(lost).IsFalse();
        await Assert.That(ledge.SupportedFraction).IsEqualTo(1f);
        await Assert.That(Approx(ledge.Edge.Position.X, 8f) && Approx(ledge.Edge.Position.Z, 3f)).IsTrue();
    }

    [Test]
    [Arguments(0.3f, false)]
    [Arguments(0.1f, true)]
    public async Task ledge_follows_a_step_down_within_the_snap_distance(float snapDistance, bool lost)
    {
        using CollisionWorld world = Build(Ground(0f, 10f, 0f), Ground(10f, 20f, -0.2f));
        var settings = new GroundSettings(snapDistance, stepHeight: 0.35f, maxSlopeDegrees: 45f);
        CharacterGround.Probe(world, settings, new Vector3(9f, 0f, 5f), out GroundHit start);
        bool found = CharacterGround.FindLedge(world, settings, start, new Vector3(2f, 0f, 0f), out GroundLedge ledge);

        await Assert.That(found).IsEqualTo(lost);
        await Assert.That(Approx(ledge.Edge.Position.X, lost ? 10f : 11f)).IsTrue();
        await Assert.That(Approx(ledge.Edge.Position.Y, lost ? 0f : -0.2f)).IsTrue();
    }

    [Test]
    public async Task ledge_walk_keeps_the_feet_height_above_the_ground()
    {
        // Feet hover 0.083 above the upper slab, so the 0.3 snap reaches 0.217 below it and misses the
        // lower slab 0.25 down, exactly as a probe from the feet would.
        using CollisionWorld world = Build(Ground(0f, 10f, 0f), Ground(10f, 20f, -0.25f));
        CharacterGround.Probe(world, s_settings, new Vector3(9.8f, 0.083f, 5f), out GroundHit start);
        bool lost = CharacterGround.FindLedge(world, s_settings, start, new Vector3(0.5f, 0f, 0f), out GroundLedge ledge);

        await Assert.That(lost).IsTrue();
        await Assert.That(Approx(ledge.Edge.Position.X, 10f)).IsTrue();
        await Assert.That(Approx(ledge.Edge.Distance, 0.083f)).IsTrue();

        var position = new Vector3(9.8f, 0.083f + FeetOffset, 5f);
        bool grounded = PlanarCapsuleSlide.MoveGrounded(world, CollisionFilter.Default, CapsuleRadius, CapsuleHalfLength,
            position, new Vector3(0.5f, 0f, 0f), 0.01f, s_settings, LedgePolicy.Stop, out Vector3 result, out GroundHit ground);
        await Assert.That(grounded).IsTrue();
        await Assert.That(ground.BodyIndex).IsEqualTo(0);
        await Assert.That(Approx(result.X, 10f)).IsTrue();
        await Assert.That(Approx(result.Y, FeetOffset)).IsTrue();
    }

    [Test]
    public async Task ledge_walk_follows_a_long_uphill_slope()
    {
        const float degrees = 20f;
        using CollisionWorld world = Build(Slope(degrees));
        CharacterGround.Probe(world, s_settings, new Vector3(8f, SlopeHeight(degrees, 8f), 10f), out GroundHit start);
        bool lost = CharacterGround.FindLedge(world, s_settings, start, new Vector3(4f, 0f, 0f), out GroundLedge ledge);

        await Assert.That(lost).IsFalse();
        await Assert.That(Approx(ledge.Edge.Position.Y, SlopeHeight(degrees, 12f))).IsTrue();
    }

    [Test]
    [Arguments(LedgePolicy.Stop)]
    [Arguments(LedgePolicy.Slide)]
    [Arguments(LedgePolicy.StepOff)]
    public async Task grounded_move_stays_on_a_slope_dropping_more_than_the_snap_distance(LedgePolicy policy)
    {
        const float degrees = 20f;
        using CollisionWorld world = Build(Slope(degrees));
        var position = new Vector3(12f, SlopeHeight(degrees, 12f) + FeetOffset, 10f);
        bool grounded = PlanarCapsuleSlide.MoveGrounded(world, CollisionFilter.Default, CapsuleRadius, CapsuleHalfLength,
            position, new Vector3(-2f, 0f, 0f), 0.01f, s_settings, policy, out Vector3 result, out GroundHit ground);

        await Assert.That(SlopeHeight(degrees, 12f) - SlopeHeight(degrees, 10f) > s_settings.SnapDistance).IsTrue();
        await Assert.That(grounded).IsTrue();
        await Assert.That(ground.BodyIndex).IsEqualTo(0);
        await Assert.That(Approx(result.X, 10f)).IsTrue();
        await Assert.That(Approx(result.Y, SlopeHeight(degrees, 10f) + FeetOffset)).IsTrue();
    }

    [Test]
    public async Task constrain_move_applies_each_ledge_policy()
    {
        using CollisionWorld world = Build(Ground());
        CharacterGround.Probe(world, s_settings, new Vector3(19f, 0f, 5f), out GroundHit start);
        var move = new Vector3(2f, 0.5f, 2f);

        Vector3 stepOff = CharacterGround.ConstrainMove(world, s_settings, start, move, LedgePolicy.StepOff);
        await Assert.That(stepOff).IsEqualTo(new Vector3(2f, 0f, 2f));

        Vector3 stop = CharacterGround.ConstrainMove(world, s_settings, start, move, LedgePolicy.Stop);
        await Assert.That(Approx(stop.X, 1f) && Approx(stop.Z, 1f) && stop.Y == 0f).IsTrue();

        Vector3 slide = CharacterGround.ConstrainMove(world, s_settings, start, move, LedgePolicy.Slide);
        await Assert.That(Approx(slide.X, 1f) && Approx(slide.Z, 2f) && slide.Y == 0f).IsTrue();
    }

    [Test]
    public async Task grounded_move_stops_at_or_steps_off_a_ledge()
    {
        using CollisionWorld world = Build(Ground());
        var position = new Vector3(19f, FeetOffset, 5f);
        var move = new Vector3(2f, 0f, 0f);

        bool stopped = PlanarCapsuleSlide.MoveGrounded(world, CollisionFilter.Default, CapsuleRadius, CapsuleHalfLength,
            position, move, 0.01f, s_settings, LedgePolicy.Stop, out Vector3 clamped, out GroundHit edge);
        await Assert.That(stopped).IsTrue();
        await Assert.That(Approx(clamped.X, 20f)).IsTrue();
        await Assert.That(Approx(clamped.Y, FeetOffset)).IsTrue();
        await Assert.That(edge.BodyIndex).IsEqualTo(0);

        bool grounded = PlanarCapsuleSlide.MoveGrounded(world, CollisionFilter.Default, CapsuleRadius, CapsuleHalfLength,
            position, move, 0.01f, s_settings, LedgePolicy.StepOff, out Vector3 airborne, out GroundHit none);
        await Assert.That(grounded).IsFalse();
        await Assert.That(airborne).IsEqualTo(new Vector3(21f, FeetOffset, 5f));
        await Assert.That(none.BodyIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task grounded_move_tracks_a_slope()
    {
        const float degrees = 20f;
        using CollisionWorld world = Build(Slope(degrees));
        var position = new Vector3(8f, SlopeHeight(degrees, 8f) + FeetOffset, 10f);

        for (int i = 0; i < 4; i++)
        {
            bool grounded = PlanarCapsuleSlide.MoveGrounded(world, CollisionFilter.Default, CapsuleRadius, CapsuleHalfLength,
                position, new Vector3(0.5f, 0f, 0f), 0.01f, s_settings, LedgePolicy.Stop, out position, out _);
            await Assert.That(grounded).IsTrue();
        }

        await Assert.That(Approx(position.X, 10f)).IsTrue();
        await Assert.That(Approx(position.Y, SlopeHeight(degrees, 10f) + FeetOffset)).IsTrue();
    }

    [Test]
    public async Task grounded_move_climbs_steps_up_to_the_step_height_and_walls_still_block()
    {
        var wall = new CollisionFilter { BelongsTo = 1u, CollidesWith = ~0u };
        using CollisionWorld world = Build(Ground(0f, 10f, 0f), Ground(10f, 12f, 0.3f), Slab(12f, 13f, 2f, wall));
        var position = new Vector3(9.8f, FeetOffset, 5f);

        bool grounded = PlanarCapsuleSlide.MoveGrounded(world, CollisionFilter.Default, CapsuleRadius, CapsuleHalfLength,
            position, new Vector3(0.5f, 0f, 0f), 0.01f, s_settings, LedgePolicy.Stop, out position, out GroundHit step);
        await Assert.That(grounded).IsTrue();
        await Assert.That(step.BodyIndex).IsEqualTo(1);
        await Assert.That(Approx(position.Y, 0.3f + FeetOffset)).IsTrue();

        PlanarCapsuleSlide.MoveGrounded(world, CollisionFilter.Default, CapsuleRadius, CapsuleHalfLength,
            position, new Vector3(3f, 0f, 0f), 0.01f, s_settings, LedgePolicy.Stop, out position, out _);
        await Assert.That(position.X <= 12f - CapsuleRadius).IsTrue();
        await Assert.That(position.X > 11.5f).IsTrue();
    }

    [Test]
    public async Task grounded_move_keeps_support_when_a_wall_deflects_it_towards_an_edge()
    {
        var wallFilter = new CollisionFilter { BelongsTo = 1u, CollidesWith = ~0u };
        // Runs along (1, 0, 1): a +Z move slides along it towards +X, past the slab's edge at x = 20.
        var wall = (Collider.CreateBox(new Vector3(3f, 2f, 0.2f), wallFilter),
            new RigidTransform(new Vector3(19.8f, 1f, 6.5f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 4f)));
        using CollisionWorld world = Build(Ground(), wall);
        var position = new Vector3(19.5f, FeetOffset, 5f);
        var move = new Vector3(0f, 0f, 3f);

        bool stepped = PlanarCapsuleSlide.MoveGrounded(world, CollisionFilter.Default, CapsuleRadius, CapsuleHalfLength,
            position, move, 0.01f, s_settings, LedgePolicy.StepOff, out Vector3 off, out _);
        await Assert.That(stepped).IsFalse();
        await Assert.That(off.X > 20f).IsTrue();

        bool grounded = PlanarCapsuleSlide.MoveGrounded(world, CollisionFilter.Default, CapsuleRadius, CapsuleHalfLength,
            position, move, 0.01f, s_settings, LedgePolicy.Stop, out Vector3 kept, out _);
        await Assert.That(grounded).IsTrue();
        await Assert.That(kept.X > 19.5f && kept.X <= 20f).IsTrue();
    }

    [Test]
    public async Task airborne_move_ignores_the_ledge_policy()
    {
        using CollisionWorld world = Build(Ground());
        var position = new Vector3(19f, 5f, 5f);
        bool grounded = PlanarCapsuleSlide.MoveGrounded(world, CollisionFilter.Default, CapsuleRadius, CapsuleHalfLength,
            position, new Vector3(2f, 0f, 0f), 0.01f, s_settings, LedgePolicy.Stop, out Vector3 result, out _);

        await Assert.That(grounded).IsFalse();
        await Assert.That(result).IsEqualTo(new Vector3(21f, 5f, 5f));
    }

    [Test]
    public async Task handle_overloads_match_the_world_overloads()
    {
        using CollisionWorld world = Build(Ground(), Slope(30f));
        CollisionWorldHandle handle = world.Handle;
        var feet = new Vector3(19f, 0.1f, 5f);
        var move = new Vector3(2f, 0f, 1f);

        bool worldProbe = CharacterGround.Probe(world, s_settings, feet, out GroundHit worldGround);
        bool handleProbe = CharacterGround.Probe(handle, s_settings, feet, out GroundHit handleGround);
        await Assert.That(handleProbe).IsEqualTo(worldProbe);
        await Assert.That(handleGround).IsEqualTo(worldGround);

        CharacterGround.SnapToGround(world, s_settings, feet, out Vector3 worldFeet, out _);
        CharacterGround.SnapToGround(handle, s_settings, feet, out Vector3 handleFeet, out _);
        await Assert.That(handleFeet).IsEqualTo(worldFeet);

        CharacterGround.FindLedge(world, s_settings, worldGround, move, out GroundLedge worldLedge);
        CharacterGround.FindLedge(handle, s_settings, handleGround, move, out GroundLedge handleLedge);
        await Assert.That(handleLedge).IsEqualTo(worldLedge);

        Vector3 worldSlide = CharacterGround.ConstrainMove(world, s_settings, worldGround, move, LedgePolicy.Slide);
        Vector3 handleSlide = CharacterGround.ConstrainMove(handle, s_settings, handleGround, move, LedgePolicy.Slide);
        await Assert.That(handleSlide).IsEqualTo(worldSlide);

        var position = feet + new Vector3(0f, FeetOffset, 0f);
        bool worldMove = PlanarCapsuleSlide.MoveGrounded(world, CollisionFilter.Default, CapsuleRadius, CapsuleHalfLength,
            position, move, 0.01f, s_settings, LedgePolicy.Slide, out Vector3 worldResult, out _);
        bool handleMove = PlanarCapsuleSlide.MoveGrounded(handle, CollisionFilter.Default, CapsuleRadius, CapsuleHalfLength,
            position, move, 0.01f, s_settings, LedgePolicy.Slide, out Vector3 handleResult, out _);
        await Assert.That(handleMove).IsEqualTo(worldMove);
        await Assert.That(handleResult).IsEqualTo(worldResult);
    }

    [Test]
    public async Task default_handle_has_no_ground()
    {
        CollisionWorldHandle handle = default;
        bool grounded = CharacterGround.Probe(handle, s_settings, Vector3.Zero, out GroundHit ground);
        await Assert.That(grounded).IsFalse();
        await Assert.That(ground.BodyIndex).IsEqualTo(-1);
    }
}
