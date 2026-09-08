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
