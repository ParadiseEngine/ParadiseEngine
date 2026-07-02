# Paradise.Physics

Stateless collision/physics query library for Paradise Engine, modeled on Unity Physics (DOTS):
no caches, no incremental state, no hidden mutability. `CollisionWorld.Build` is a pure function
of its inputs; queries allocate nothing and are safe from any thread.

Phase 1 scope: **static colliders + closest-hit queries**.

- Shapes: sphere, capsule (Y-aligned), box — fixed-size `Collider` tagged union.
- Queries: `CastRay`, `CastCollider` (linear sweep), `CalculateDistance`.
- Filtering: Unity-style `CollisionFilter` (`BelongsTo` / `CollidesWith` masks + `GroupIndex` override).
- Narrowphase: analytic per-primitive raycasts; GJK distance (Voronoi simplex, core-shape + radius)
  with linear conservative advancement for shape casts.
- Broadphase: linear AABB scan (static sets are small in phase 1); a BVH built on
  `Paradise.BLOB`'s `BlobTree` is the planned upgrade behind the same API.

## Conventions

- Right-handed, Y-up, −Z forward, meters (`System.Numerics` math). No handedness conversion anywhere.
- `RigidTransform` = rotation + translation only; fold scale into geometry before `Build`.
- Ray starting inside a collider: hit at `Fraction = 0`, normal facing back along the ray.
- Cast starting in contact/overlap: hit at `Fraction = 0`, normal = depenetration direction; never NaN.
- Equal-fraction ties resolve to the lowest body index (order-deterministic results).

## Determinism

Queries use scalar float arithmetic and `MathF.Sqrt` only — no transcendental functions.
Same binary + same hardware ⇒ bit-identical results. Cross-ISA determinism is out of scope.

## Deferred (phase 2+)

BVH broadphase, dynamics/solver, contact manifolds & EPA, compound/mesh/convex-hull colliders,
collector variants (all-hits/any-hit), point-distance & AABB-overlap queries, rotational sweeps.
