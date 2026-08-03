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

- [hits: 1] **`Queue.WriteBuffer` rejects any size that is not a multiple of 4, and the rejection
  is SILENT** — it is validated on the queue, not at draw time, so the upload simply never lands
  and every draw reading that region renders whatever the buffer held before (zeros on a fresh
  buffer, last frame's geometry on a reused one). `NoesisRenderDevice` staged Noesis's mapped
  blocks at their exact mapped length; indices are 16-bit, so any frame with an ODD total index
  count produced a size like 18006 and lost its **entire** index block. In the game's map that
  meant ~1400 rectangles (6 indices each, an even total) rendered fine until ONE filled
  `DrawGeometry` added a 3-index triangle fan — the whole page then smeared into garbage
  triangles, which read like a path-rendering bug and is really an upload-alignment bug. Vertices
  never tripped it because every `NoesisShaderCatalog.AttrSizes` entry is a multiple of 4. Fixed
  2026-08-04 (issue #129): `WriteAligned` rounds the written length up to 4 — safe because the
  sub-allocation cursors already advance by `Align4`, so the padding lands in a gap no draw
  indexes into. **Debugging trap that cost most of the time here**: nothing pumps Dawn's
  uncaptured-error callback, so the validation message never appeared, and the frame just came
  back as the clear colour. `device.PushErrorScope(ErrorFilter.Validation)` +
  `PopErrorScopeSync` around a frame prints it immediately — reach for an error scope FIRST when
  a WebGPU frame renders empty or stale, before theorising about buffer lifetimes. Guarded by
  `a_dense_rectangle_field_plus_a_filled_path_renders_without_corrupting_the_frame`, which
  asserts the error scope is clean AND that the frame really had an unaligned map.

- [hits: 1] **"Path too complex to render. Please split the geometry into several paths" comes
  from the Noesis NATIVE core, not from Paradise** — the string lives in `Noesis.dylib`, and
  `NoesisRenderDevice` has no path-complexity cap of its own. There is no engine-side knob to
  raise; a `Path` with hundreds of figures must be split into several paths (or drawn as
  separate primitives) by the caller. Do not go looking for the limit in the render device.

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

- [hits: 1] **`NoesisRenderDevice` offscreen surfaces must be created in `_colorFormat`, never
  hardcoded RGBA8** — every pipeline's color target state is `_colorFormat`, and the same
  pipeline cache serves offscreen and onscreen passes. On a BGRA8 host (the headless
  renderer's offscreen target, typical windowed swapchains) a hardcoded-RGBA8 offscreen
  surface makes every opacity-group/effect frame's pass invalid, and **Dawn drops the ENTIRE
  command buffer silently** — no `UncapturedErrorCallback` fires without an instance event
  pump, so the symptom is a backbuffer frozen at the last pre-Noesis frame while every CPU
  loop keeps "succeeding" (sim ticks, RecordOverlay, Submit all normal). Debugging trap on
  top: a "clear landed" pixel probe must check ALPHA (zero-initialized texture reads the same
  0 as a dark clear color in RGB); a probe that can't distinguish "frame dropped" from
  "frame landed dark" hides exactly this failure. Fixed 2026-08-01 (issue #126 branch):
  `CreateNativeTexture(renderTarget: true)` now uses `_colorFormat`; guarded by
  `frames_after_a_noesis_overlay_still_reach_the_target(BGRA8Unorm)` +
  `concurrent_sim_ticks_do_not_freeze_the_target` with Noesis-content pixel asserts. Related:
  `WebGpuDevice` now installs a `DeviceLostCallback` so silent device loss is at least loggable.

- [hits: 1] **toktx `--normal_mode` stores X in RGB and Y in ALPHA ("RRRG" two-channel
  layout), NOT a standard 3-channel normal map.** BC5 transcoding (`KTX_TTF_BC5_RG`) maps it
  to R=X, G=Y natively — but a raw RGBA32 transcode yields (X,X,X,Y), so a shader sampling
  R/G would read (X,X) and shade garbage. `Ktx2Transcoder`'s RGBA32 fallback swizzles normal
  maps to (X, Y, 255, 255) so both paths share BC5 channel semantics (discovered by the
  fixture golden test: expected B=255, got B=127=X). The game pipeline's UastcNormalLinear
  preset always passes --normal_mode, so the two-channel layout is contractual.
