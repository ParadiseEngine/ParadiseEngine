# Project Lessons — ParadiseEngine

## Paradise.BLOB

- [hits: 1] **`StructBuilder<T>` used to silently DROP plain (non-builder) fields set through
  `builder.Value.X = ...`** — its `BuildImpl` only ran the registered field builders and never
  copied `_value` into the reserved (zero-initialized) data region, unlike `ValueBuilder<T>`
  (which does `data = _value`). Nobody noticed because every existing consumer either used
  `ValueBuilder` for plain structs or covered every field of a `StructBuilder` struct with a
  field builder (`SetArray`/`SetTree`/`SetPointer`). Fixed 2026-07-02: `StructBuilder.BuildImpl`
  now writes `data = _value` first, then lets field builders overwrite their regions — matching
  `ValueBuilder` semantics. Guarded by
  `TestNativeBlobAssetReference.should_round_trip_struct_with_array_through_native_memory`
  (mixed plain `Header` + `BlobArray` field).

- [hits: 1] **`Paradise.BLOB.Test` uses its own NUnit-compat shim (`AssertCompat.cs`:
  `Assert.That(x, Is.EqualTo(y))`, `Assert.AreEqual`, `Assert.Catch<T>`), NOT TUnit's fluent
  async API** — `await Assert.That(x).IsEqualTo(y)` does not compile there (the local `Assert`
  class shadows TUnit's). Tests are synchronous `void` methods. Every other test project in the
  repo (ECS, Physics, BT) uses the TUnit fluent async style. Also: `Span<T>` locals cannot cross
  `await` boundaries (CS4007) — in fluent-async test projects, use arrays and let them convert
  to spans at call sites.

## Paradise.Physics

- [hits: 1] **An invalid (`default`) `CollisionWorldHandle` is SAFE for casts but WRONG for
  support clamping**: `CastRay`/`CastCollider`/`CalculateDistance` return false (queries miss →
  unobstructed movement, the intended "no collision world" semantics), but
  `PlanarGroundSupport.Clamp` interprets "no support hit anywhere" as "stay put" and returns
  `from` — an unguarded call FREEZES the mover instead of letting it move freely.
  `PlanarSphereDynamics` guards internally (`!statics.IsValid` skips the support clamp);
  hand-written slide code must guard with `handle.IsValid` before calling `Clamp` (see the
  game's `MovementSystem.Slide`).

## Paradise.ECS

- [hits: 1] **Adding a required `With<T>` to an existing `[Queryable]` silently unmatches every
  already-spawned entity that lacks T** — no error, the query just returns fewer rows and
  systems quietly skip those entities. When growing a queryable (e.g. adding
  `With<PhysicsWorldRef>`/`With<SimulationContext>`), sweep EVERY spawn site: runner spawn
  helpers, test EntityBuilder chains, and scene-bridge spawns must all add the new component.

- [hits: 1] **`SingleWriterAnalyzer` (PECS3008) write detection covers three field shapes**:
  non-readonly `ref T`, `Span<T>`, and queryable composition fields (Data/ChunkData/Segments
  nested in a `[Queryable]` type — every `With<T>` without `IsReadOnly`/`QueryOnly` counts as a
  write). When adding a new generated field kind that can write components, extend
  `GetWrittenComponent`/`GetQueryableWrittenComponents` or the analyzer goes blind to it.
