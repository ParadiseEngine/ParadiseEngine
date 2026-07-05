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

- [hits: 2] **An invalid (`default`) `CollisionWorldHandle` must mean "unobstructed", never
  "frozen"**: casts return false (miss), and support clamping must follow the same rule.
  `PlanarGroundSupport.Clamp`'s handle overload originally interpreted "no support hit
  anywhere" as "stay put" and FROZE movers on an invalid handle; PR #65 review caught it and
  the overload now guards `!statics.IsValid → accept the move` (like
  `PlanarSphereDynamics.ClampToSupport` always did). When adding new handle-based queries,
  keep the invariant: invalid handle = every query misses = movement unobstructed.

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

## Paradise.Rendering

- [hits: 1] **WebGPUSharp 0.5.2's type-specific `SurfaceDescriptor(ref SurfaceSource*FFI)`
  constructors do NOT stamp `Chain.SType`** — Dawn then rejects the surface with
  "Unexpected chained struct of type SType::0" and `Surface.GetCapabilities` returns null.
  Every `SurfaceSource*FFI` must set
  `Chain = new ChainedStruct { SType = SType.SurfaceSource<Kind> }` explicitly (all four
  paths in `SurfaceFactory` now do). This was latent from M0b: the Win32/Xlib/Wayland paths
  had never actually been executed windowed — the first real windowed run (macOS Metal,
  PR "renderer-macos-windowed") exposed it. Symptom is at surface-capability query time,
  not at CreateSurface (which succeeds).

- [hits: 1] **SDL3 (ppy.SDL3-CS 2026.320.0) binds `SDL_Metal_CreateView/GetLayer/DestroyView`
  with plain `IntPtr`** (no `SDL_MetalView` wrapper type). Windowed macOS = create the Metal
  view AFTER `SDL_CreateWindow`, hand `SDL_Metal_GetLayer` to the Cocoa surface source, and
  `SDL_Metal_DestroyView` only after the renderer/surface is disposed.

- [hits: 1] **toktx `--normal_mode` stores X in RGB and Y in ALPHA ("RRRG" two-channel
  layout), NOT a standard 3-channel normal map.** BC5 transcoding (`KTX_TTF_BC5_RG`) maps it
  to R=X, G=Y natively — but a raw RGBA32 transcode yields (X,X,X,Y), so a shader sampling
  R/G would read (X,X) and shade garbage. `Ktx2Transcoder`'s RGBA32 fallback swizzles normal
  maps to (X, Y, 255, 255) so both paths share BC5 channel semantics (discovered by the
  fixture golden test: expected B=255, got B=127=X). The game pipeline's UastcNormalLinear
  preset always passes --normal_mode, so the two-channel layout is contractual.
