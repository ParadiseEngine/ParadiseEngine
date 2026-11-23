# Paradise.Physics

Collision queries and sphere dynamics for Paradise Engine. `CollisionWorld.Build` copies static
colliders into one native blob with a deterministic BVH. Queries allocate nothing and may run
concurrently; rebuild the world when static bodies change.

- Shapes: spheres, Y-aligned capsules and boxes.
- Queries: raycasts, linear shape casts and closest surface distance.
- Filtering: layer masks plus a `GroupIndex` override.
- Narrowphase: analytic raycasts, GJK distance and conservative advancement for shape casts.
- Dynamics: rigid spheres with gravity, contact impulses, friction and spin; planar capsule
  sliding and support containment. Callers own state and integrate sphere orientation.
- Character ground: grounded probes with a slope limit, ledge detection and snap-to-ground.

## Character ground support

Walkable colliders belong to `CollisionFilter.GroundLayer` (bit 31, reserved; game layers use
bits 0–30). Author floors with `CollisionFilter.Ground`, or add the bit to a body's `BelongsTo` when
it is also on game layers. `CollisionFilter.Default` belongs to every layer, so it is ground too:
keep walls and props out of ground support with `BelongsTo = ~CollisionFilter.GroundLayer` (or
any mask without the bit). `GroundSettings` probes with `CollisionFilter.GroundProbe`, which sees
only that layer.

- `CharacterGround.Probe` casts a vertical ray from `StepHeight` above the feet to
  `SnapDistance` below them and reports the contact, normal, body and feet height above it.
  Surfaces steeper than the slope limit are reported but are not ground, and neither is a body the
  probe starts inside, whose surface it cannot see.
- `CharacterGround.SnapToGround` places the feet on that ground, or keeps their height when there
  is none so the game decides how to fall.
- `CharacterGround.FindLedge` follows the ground along a planar move, keeping the feet's starting
  height above it, and reports the supported fraction and the ground at the edge; `ConstrainMove` turns that into a `LedgePolicy`: step off,
  stop at the edge, or stop and slide along it axis by axis.
- `PlanarCapsuleSlide.MoveGrounded` combines the capsule slide (raised by `StepHeight` while
  grounded, so floors and low steps do not block it), the ledge policy and snap-to-ground. A grounded
  move places the feet on the ground followed along it, so long moves over walkable slopes stay grounded.

Support is measured under the feet point, so a character is over an edge once its centre is.
`PlanarGroundSupport` keeps its axis-by-axis clamp, which treats anything its filter hits as support.

## Conventions

- Right-handed, Y-up, −Z forward, metres; `RigidTransform` carries rotation and translation.
  Bake scale into geometry.
- Rays starting inside report fraction zero with a normal opposing the ray.
- Casts starting in contact report fraction zero with a depenetration normal.
- Equal fractions/distances select the lowest body index.
- Dispose the world after use. Borrowed `CollisionWorldHandle` values must not outlive it;
  default handles represent no world and queries miss.
- Identical inputs, binary and hardware produce identical results; cross-ISA determinism is
  not guaranteed.

## Future scope

Contact manifolds, EPA, additional dynamic bodies, compound/mesh/convex-hull colliders,
all-hit/any-hit collectors, point-distance and AABB-overlap queries, and rotational sweeps.
