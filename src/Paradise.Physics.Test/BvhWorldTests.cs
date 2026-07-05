using System.Numerics;

namespace Paradise.Physics.Test;

/// <summary>
/// Differential guard for the BLOB/BVH-backed <see cref="CollisionWorld"/>: world queries over a
/// large random body set must agree exactly with brute-force per-collider narrowphase loops
/// (same hits, same fractions, lowest-index tie-break).
/// </summary>
public class BvhWorldTests
{
    private static (CollisionWorld World, Collider[] Colliders, RigidTransform[] Transforms) RandomWorld(int count, int seed)
    {
        var random = new Random(seed);
        var colliders = new Collider[count];
        var transforms = new RigidTransform[count];
        for (int i = 0; i < count; i++)
        {
            colliders[i] = (i % 3) switch
            {
                0 => Collider.CreateSphere(0.2f + (float)random.NextDouble()),
                1 => Collider.CreateCapsule(0.15f + (float)random.NextDouble() * 0.5f, 0.1f + (float)random.NextDouble()),
                _ => Collider.CreateBox(new Vector3(
                    0.2f + (float)random.NextDouble(),
                    0.2f + (float)random.NextDouble(),
                    0.2f + (float)random.NextDouble())),
            };
            var axis = Vector3.Normalize(new Vector3(
                (float)(random.NextDouble() * 2.0 - 1.0),
                (float)(random.NextDouble() * 2.0 - 1.0),
                (float)(random.NextDouble() * 2.0 - 1.0) + 1e-3f));
            transforms[i] = new RigidTransform(
                new Vector3(
                    (float)(random.NextDouble() * 2.0 - 1.0) * 30f,
                    (float)(random.NextDouble() * 2.0 - 1.0) * 5f,
                    (float)(random.NextDouble() * 2.0 - 1.0) * 30f),
                Quaternion.CreateFromAxisAngle(axis, (float)(random.NextDouble() * Math.PI * 2.0)));
        }

        return (CollisionWorld.Build(colliders, transforms), colliders, transforms);
    }

    [Test]
    public async Task world_raycasts_match_brute_force_over_200_random_bodies()
    {
        (CollisionWorld world, Collider[] colliders, RigidTransform[] transforms) = RandomWorld(200, seed: 9001);
        using CollisionWorld _ = world;
        var random = new Random(4242);

        for (int query = 0; query < 300; query++)
        {
            var start = new Vector3(
                (float)(random.NextDouble() * 2.0 - 1.0) * 40f,
                (float)(random.NextDouble() * 2.0 - 1.0) * 8f,
                (float)(random.NextDouble() * 2.0 - 1.0) * 40f);
            var end = new Vector3(
                (float)(random.NextDouble() * 2.0 - 1.0) * 40f,
                (float)(random.NextDouble() * 2.0 - 1.0) * 8f,
                (float)(random.NextDouble() * 2.0 - 1.0) * 40f);
            var input = new RaycastInput { Start = start, End = end, Filter = CollisionFilter.Default };

            bool worldHit = world.CastRay(input, out RaycastHit viaBvh);

            bool bruteHit = false;
            var brute = default(RaycastHit);
            float best = float.PositiveInfinity;
            int bestBody = int.MaxValue;
            for (int i = 0; i < colliders.Length; i++)
            {
                if (!ColliderQueries.RayCollider(input, colliders[i], transforms[i], out RaycastHit hit)) continue;
                if (hit.Fraction > best || (hit.Fraction == best && i > bestBody)) continue;
                best = hit.Fraction;
                bestBody = i;
                bruteHit = true;
                brute = hit;
                brute.BodyIndex = i;
            }

            await Assert.That(worldHit).IsEqualTo(bruteHit);
            if (!worldHit) continue;
            await Assert.That(viaBvh.BodyIndex).IsEqualTo(brute.BodyIndex);
            await Assert.That(viaBvh.Fraction).IsEqualTo(brute.Fraction); // bitwise: same narrowphase call
        }
    }

    [Test]
    public async Task world_collider_casts_match_brute_force_over_100_random_bodies()
    {
        (CollisionWorld world, Collider[] colliders, RigidTransform[] transforms) = RandomWorld(100, seed: 7007);
        using CollisionWorld _ = world;
        var random = new Random(1313);
        var caster = Collider.CreateCapsule(0.35f, 0.45f);

        for (int query = 0; query < 100; query++)
        {
            var start = new Vector3(
                (float)(random.NextDouble() * 2.0 - 1.0) * 40f,
                (float)(random.NextDouble() * 2.0 - 1.0) * 6f,
                (float)(random.NextDouble() * 2.0 - 1.0) * 40f);
            var end = new Vector3(
                (float)(random.NextDouble() * 2.0 - 1.0) * 40f,
                start.Y,
                (float)(random.NextDouble() * 2.0 - 1.0) * 40f);
            var input = new ColliderCastInput { Collider = caster, Orientation = Quaternion.Identity, Start = start, End = end };

            bool worldHit = world.CastCollider(input, out ColliderCastHit viaBvh);

            bool bruteHit = false;
            float best = float.PositiveInfinity;
            int bestBody = int.MaxValue;
            for (int i = 0; i < colliders.Length; i++)
            {
                if (!ColliderQueries.CastCollider(input, colliders[i], transforms[i], out ColliderCastHit hit)) continue;
                if (hit.Fraction > best || (hit.Fraction == best && i > bestBody)) continue;
                best = hit.Fraction;
                bestBody = i;
                bruteHit = true;
            }

            await Assert.That(worldHit).IsEqualTo(bruteHit);
            if (!worldHit) continue;
            await Assert.That(viaBvh.BodyIndex).IsEqualTo(bestBody);
            await Assert.That(viaBvh.Fraction).IsEqualTo(best); // bitwise
        }
    }

    [Test]
    public async Task handle_queries_are_bitwise_identical_to_the_class_api()
    {
        (CollisionWorld world, Collider[] _, RigidTransform[] _) = RandomWorld(150, seed: 3141);
        using CollisionWorld disposeWorld = world;
        CollisionWorldHandle handle = world.Handle;
        var random = new Random(2718);
        int hits = 0;

        for (int query = 0; query < 200; query++)
        {
            var start = new Vector3(
                (float)(random.NextDouble() * 2.0 - 1.0) * 40f,
                (float)(random.NextDouble() * 2.0 - 1.0) * 8f,
                (float)(random.NextDouble() * 2.0 - 1.0) * 40f);
            var end = new Vector3(
                (float)(random.NextDouble() * 2.0 - 1.0) * 40f,
                (float)(random.NextDouble() * 2.0 - 1.0) * 8f,
                (float)(random.NextDouble() * 2.0 - 1.0) * 40f);

            var rayInput = new RaycastInput { Start = start, End = end, Filter = CollisionFilter.Default };
            bool classHit = world.CastRay(rayInput, out RaycastHit classRay);
            bool handleHit = handle.CastRay(rayInput, out RaycastHit handleRay);
            await Assert.That(handleHit).IsEqualTo(classHit);
            if (classHit)
            {
                hits++;
                await Assert.That(handleRay.Fraction).IsEqualTo(classRay.Fraction);
                await Assert.That(handleRay.BodyIndex).IsEqualTo(classRay.BodyIndex);
            }

            var castInput = new ColliderCastInput { Collider = Collider.CreateSphere(0.4f), Orientation = Quaternion.Identity, Start = start, End = end };
            bool classCast = world.CastCollider(castInput, out ColliderCastHit classCastHit);
            bool handleCast = handle.CastCollider(castInput, out ColliderCastHit handleCastHit);
            await Assert.That(handleCast).IsEqualTo(classCast);
            if (classCast)
            {
                await Assert.That(handleCastHit.Fraction).IsEqualTo(classCastHit.Fraction);
                await Assert.That(handleCastHit.BodyIndex).IsEqualTo(classCastHit.BodyIndex);
            }
        }

        await Assert.That(hits > 0).IsTrue();
    }

    [Test]
    public async Task default_handle_is_invalid_and_misses_everything()
    {
        CollisionWorldHandle handle = default;
        await Assert.That(handle.IsValid).IsFalse();
        bool hit = handle.CastRay(new RaycastInput { Start = Vector3.Zero, End = Vector3.UnitX * 100f, Filter = CollisionFilter.Default }, out _);
        await Assert.That(hit).IsFalse();
        bool cast = handle.CastCollider(new ColliderCastInput { Collider = Collider.CreateSphere(1f), Orientation = Quaternion.Identity, Start = Vector3.Zero, End = Vector3.UnitX }, out _);
        await Assert.That(cast).IsFalse();

        // Invalid = unobstructed everywhere: Clamp must accept the full move (Y from `from`),
        // never freeze the mover at `from` because no support ray can hit.
        Vector3 clamped = PlanarGroundSupport.Clamp(handle, CollisionFilter.Default,
            from: new Vector3(1f, 0.9f, 1f), to: new Vector3(5f, 2f, 7f), probeDepth: 10f);
        await Assert.That(clamped).IsEqualTo(new Vector3(5f, 0.9f, 7f));
    }

    [Test]
    public async Task empty_world_misses_everything_and_disposes_cleanly()
    {
        using CollisionWorld world = CollisionWorld.Build([], []);
        bool rayHit = world.CastRay(new RaycastInput { Start = Vector3.Zero, End = Vector3.UnitX, Filter = CollisionFilter.Default }, out _);
        await Assert.That(rayHit).IsFalse();
        await Assert.That(world.NumBodies).IsEqualTo(0);
    }
}
