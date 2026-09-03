# Project Lessons — ParadiseEngine

## Paradise.Authoring

- [hits: 1] **`AuthoredModel.Read` SILENTLY skips any `[Authored]` property whose type it cannot
  map — no diagnostic, no runtime warning — and the field vanishes from schema AND generated
  reader alike.** `Nullable<T>` value types (`int?`, `float?`) hit this until 2026-08-28
  (`SchemaTypeOf` sees `SpecialType.None`): `EnvironmentData.ShadowMapSize` authored 4096,
  materialized null, and the renderer silently ran its 1024 default — the shadow-acne bands PR
  #141 had fixed came back with no error anywhere. Fixed by unwrapping `Nullable<T>` leaves to
  their underlying type (absent/JSON-null keeps the record's null initializer, which is what
  "unset leaves the default" means); guarded by
  `nullable_value_fields_materialize_when_present_and_stay_null_when_absent`. **Rule**: when a
  scene-authored value "doesn't apply" at runtime, diff the regenerated `authoring-schema.json`
  for the field FIRST — a field missing there was skipped by the generator, and the skip path
  still exists for genuinely unsupported types. Consider it also when adding any new field shape
  to a contract record.

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

## C# / .NET gotchas

- [hits: 1] **`<paramref>` in a TYPE-level XML doc is CS1734, and this repo's warnings-as-errors
  turns that into a build failure.** Hit 2026-09-04 documenting `ISceneDocumentStore`'s
  absolute-`UPath` contract on the interface rather than on each method: `<paramref name="path"/>`
  resolves against the *declaring member's* parameters, and a type has none. The message names the
  parameter, not the placement, so it reads like a typo. **Rule**: in a `<summary>`/`<remarks>` on
  a type, name parameters with `<c>path</c>`; `<paramref>` only inside a method's own doc.

- [hits: 1] **`MemoryMarshal.CreateReadOnlySpan(ref local, 1)` over a stack local COMPILES even
  when the span escapes the method** — the parameter is `scoped ref`, so ref-safety analysis does
  not tie the returned span's lifetime to the local, and a `private ReadOnlySpan<byte> AsBytes()
  { var copy = _data; return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref copy, 1)); }`
  helper returns a span over a dead frame. Found 2026-08-30 in `RuntimeNodeFactory<T>` (a
  DRY-motivated extraction of two identical span expressions): every behavior node's authored
  defaults became stack garbage — `IndexOutOfRangeException` in `ProbeNode.Tick` and dozens of
  wrong-NodeState failures, nothing pointing at serialization. **Rule**: a span created over a
  local via `MemoryMarshal.CreateSpan`/`CreateReadOnlySpan`/`AsBytes` must be CONSUMED in the
  same method — never returned, even from a private helper; dedupe with a
  `CopyTo(destination)`-shaped helper instead. Symptom signature: garbage node/struct data with
  no error at the write site.

## Paradise.BT

- [hits: 1] **The BT generators/analyzers match core types BY FULL-NAME STRING (`Paradise.BT.INode`,
  formerly `INodeData`) — renaming the core type does not break the build, it silently EMPTIES the
  output.** Found 2026-08-30: after the INodeData→INode rename, `BindingGenerator` resolved zero
  node access and emitted a blackboard commented "This tree touches nothing"; the only symptom was
  a CS1739 at the Bind call site (missing parameter), nowhere near the cause. `DuplicateGuidAnalyzer`
  and `BlackboardAccessAnalyzer` key on the same constant and just stop reporting. **Rule**: when
  renaming any type the generators reference, grep `src/Paradise.BT.Generators` for the old
  full name first — and treat a generated "touches nothing" blackboard as a resolution failure,
  not as truth.

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

- [hits: 1] **Dropping `Paradise.Rendering.Pbr`'s ProjectReference to `Paradise.Rendering.WebGPU`
  (the IRenderer extraction, 2026-08-13) breaks whoever was getting the backend TRANSITIVELY, and
  the only in-repo casualty is `Paradise.Rendering.Pbr.Test`** — its GPU tests call
  `WebGpuRenderer.CreateHeadless`, so it now needs its own explicit ProjectReference (added).
  Checked the sibling game repos before worrying about downstream fallout: ShiningPie.Launcher,
  ParadiseTown.Launcher, ImmortalCultivation.Launcher and ParadiseGodotEditor's
  Paradise.Sample.Runtime **all already declare `Paradise.Rendering.WebGPU` explicitly**, so the
  0.7.0 bump needs no csproj patching there — sweep for the pattern rather than assuming a
  release-note-worthy break. Note the workspace source override converts PackageReferences to
  ProjectReferences one-for-one, so it neither masks nor creates this class of failure.

- [hits: 2] **`Queue.WriteBuffer` rejects any size that is not a multiple of 4, and the rejection
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
  **Re-hit 2026-08-12 (GPU skinning):** an invalid PIPELINE (see the loader-visibility entry
  below) blanked entire frames — every pass dropped at submit, background included, mean pixel
  exactly 0 — with nothing on stderr even in a standalone console run. Hours of shader-level
  bisecting before the object-validity check; the error-scope rule above would have named the
  failing object in one run. Addendum to the rule: *mean exactly 0 including the clear colour
  means an invalid object poisoned the whole command buffer — check createRenderPipeline /
  createBindGroupLayout validity before reading a single line of shader code.*

- [hits: 1] **`ShaderProgramLoader.BuildLayout`'s binding-type mappings encode the visibility of
  their FIRST consumer, not a general rule — and its doc comment lies about it.** The comment
  says "Visibility is Vertex|Fragment for every entry", but `StructuredBuffer<T>` was hardcoded
  `ShaderStage.Fragment` because the Forward+ cluster masks (fragment-read) were the only storage
  buffer when it was written. The GPU-skinning joint palette — read from the VERTEX stage — then
  failed createRenderPipeline silently (see the re-hit note above). Read-only storage is legal in
  the vertex stage; only read_write is prohibited there. When a new binding is read from a stage
  no existing shader uses, verify the `BuildLayout` switch actually grants that stage.

- [hits: 1] **A Slang program's vertex entry point and its vertex layout must travel together,
  and until 2026-08-12 they didn't.** `CreatePipeline`/`CreateDepthOnlyPipeline` picked the
  vertex module last-wins (no `break`) while `VertexLayouts` reflected only the FIRST entry
  point — so authoring a second `[shader("vertex")]` in any .slang silently repointed every
  existing pipeline at it with a mismatched stride: all draws empty, no error. Now:
  `vertexEntryPoint` parameter (first-wins default) + `ShaderProgramDesc.VertexBuffersByEntryPoint`.
  Trap that re-blacked the frame once during the fix: `PbrRenderer` REBUILDS its
  `ShaderProgramDesc`s for dynamic-offset patching, and any init-only property not explicitly
  carried across a `new ShaderProgramDesc(...)` rebuild vanishes.

- [hits: 1] **When bisecting a dead draw, remove the RESOURCE REFERENCE, not just its use.** A
  probe that calls the suspect function but discards the result (guarded dead code, to keep the
  bind group stable) still keeps the binding in the entry point's interface — and therefore keeps
  the very validation failure being hunted. "Shader ignores the palette" and "entry point never
  references the palette buffer" are different experiments; only the second discriminated here.

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

- [hits: 1] **A Noesis `View` routes NOTHING you don't explicitly dispatch, and drops the rest
  in total silence.** `MouseMove`/`MouseButtonDown`/`MouseButtonUp`/`MouseWheel`/`MouseHWheel`/
  `Scroll`/`HScroll`/`Touch*` are each a separate call; a `UiEventKind` that lands in
  `NoesisViewCore.UiInputHalf.Handle`'s `default:` arm produces no warning and no visual clue —
  the UI simply never reacts. `UiEventKind.Scroll` sat in that arm from 0.6.0 until 0.6.2, so
  NO host could scroll a ScrollViewer or wheel-zoom a custom element, on any platform. **Rule**:
  when adding a `UiEventKind`, grep every `IUiInput.Handle`, not just ImGui's. Note
  `MouseWheel`/`MouseHWheel` take `(x, y, rotation)` and hit-test at that point while the
  `UiEvent` contract reuses X/Y for the delta — the view half has to remember the last pointer
  position. Rotation is Win32 units: 120 per notch, positive = forward / to the right.

- [hits: 1] **A bare Noesis view has no theme, so templated controls get no template — and a
  `ScrollViewer` without one silently cannot scroll.** `NoesisViewCore` only calls
  `GUI.LoadApplicationResources` when `Theme/NoesisTheme.DarkBlue.xaml` sits next to the XAML.
  Without it a test `ScrollViewer` lays out fine (`ActualHeight` is correct) but reports
  `ExtentHeight == ViewportHeight == 0` forever: no template ⇒ no `ScrollContentPresenter` ⇒ no
  `IScrollInfo`. **Rule**: test input plumbing against the routed event
  (`element.MouseWheel += …`, reading `MouseWheelEventArgs.Delta`/`.Orientation`), not against a
  templated control's state — it is the honest assertion AND needs no WebGPU device.

## Dear ImGui / Paradise.Ui.ImGui

- [hits: 1] **`ImGui.GetID(string)` outside a frame SEGFAULTS, and inside one it is seeded by the
  CURRENT WINDOW's id stack.** It reads `GImGui->CurrentWindow->IDStack`, which is null between
  `Render` and the next `NewFrame` — the process dies with no managed exception and no output, so
  it looks like the renderer or the capture path. Found 2026-09-04: `EditorDockspace.HasNode`
  called it after the frame in the host's headless smoke run, exit 139 with an empty log.
  **Rule**: an id that must survive across frames, be stable wherever it is computed, and be
  readable between frames — a dockspace id, a persisted node id — comes from
  `ImGuiP.ImHashStr(name)`, which is a pure function of the string. Reserve `GetID` for ids that
  are genuinely scoped to the window being drawn. The stack-seeding half matters too: taking a
  dockspace id with `GetID` silently changes it if the call ever moves inside a `Begin`/`End`,
  which loses the saved layout with no error.

- [hits: 1] **Every Hexa ImGui SIBLING ships its own native with its own statically-linked Dear
  ImGui, and therefore its own `GImGui`.** `cimguizmo`, `cimplot` and `cimnodes` cannot see the
  context `cimgui` created, so `ImGuizmo.SetImGuiContext(ImGui.GetCurrentContext())` (and the
  ImPlot/ImNodes equivalents, plus their own `CreateContext`) is mandatory setup, not a nicety.
  Verified 2026-09-04 by deleting the `Attach()` call: `ImGuizmo.BeginFrame()` segfaults the TEST
  HOST — exit 139, no managed stack, and the affected tests simply never report, so the run looks
  like "16 passed" beside a non-zero EXIT CODE rather than a failure. Read the exit code, not the
  summary line: this failure mode is invisible in "failed: 0". **Rule**: wire the context in one
  place per sibling and cover it with a test; a missing handoff cannot be caught by reading a
  stack trace, because there is not one.

## Concurrency testing

- `[hits: 1]` **A stress-loop race test can pass against genuinely broken code — use Coyote and
  prove the test fails without the fix.** A check-then-enqueue defect in the renderer's capture
  queue (a request could be accepted after the drain had passed, leaving a task nobody would ever
  complete) survived a hand-written test that spawned a poster thread racing `Dispose` 64 times per
  attempt over 6 attempts: **3 runs out of 3 passed** against the broken build. The window is a few
  instructions and disposal must both flag and drain inside it. The Coyote test on the same build
  failed inside 200 iterations with the exact invariant violation. Write the systematic test, then
  reintroduce the defect to confirm it actually catches it — otherwise the guard is unverified.

- `[hits: 1]` **Coyote reports blocking joins as potential deadlocks, even for correct code.**
  `Task.WaitAll` parks a thread and Coyote cannot tell that from a hang, so every test came back
  "Potential deadlock or hang detected" against a correct implementation, and a 200-iteration run
  took over 10 minutes against the 5s deadlock timeout. `Paradise.ECS.CoyoteTest` works around it
  with `WithPotentialDeadlocksReportedAsBugs(false)`; the better fix is `async` tests that `await
  Task.WhenAll`, which lets Coyote schedule the join, keeps hang detection meaningful, and runs in
  seconds.

- `[hits: 1]` **Coyote can only schedule MANAGED concurrency — extract it away from native calls
  first.** `Paradise.Rendering.WebGPU` blocks in several places, but all of them are Dawn
  (`OnSubmittedWorkSync`, `MapSync`, `RequestAdapterSync`, `CreateRenderPipelineSync`) and are
  invisible to the scheduler. Only the capture queue's lock/flag/drain were managed, so they moved
  into `Internal/CaptureQueue.cs` with zero native calls. Check what is actually schedulable before
  planning a systematic test.

## WebAssembly / browser backend

- [hits: 1] **`JSHost.ImportAsync` resolves a RELATIVE module URL against `_framework/`, not
  against the page** — the dynamic `import()` happens inside the runtime's own JS module, so
  `"./_content/Paradise.Rendering.Browser/paradise-webgpu.js"` is fetched from
  `_framework/_content/...` and 404s. The failure is doubly confusing because the 404 is silent in
  the .NET log and the *next* `[JSImport]` call is what throws ("ES6 module X was not imported yet,
  please call JSHost.ImportAsync() first"), pointing at the wrong line. Fix used in
  `BrowserRenderer.CreateAsync`: resolve the URL against `document.baseURI`
  (`JSHost.GlobalThis.GetPropertyAsJSObject("document").GetPropertyAsString("baseURI")` + `new
  Uri(base, relative)`) before calling ImportAsync — that also keeps apps hosted under a sub-path
  working, which a hardcoded leading `/` or `../` would not. App-side modules can instead be
  resolved page-side (`new URL(name, document.baseURI).href`) and passed in, which the sample does.

- [hits: 1] **Static web asset fingerprinting only touches `_framework/`, never `wwwroot/` or
  `_content/`.** Verified by publishing the same app with `WasmFingerprintAssets` true and false:
  `_content/Paradise.Rendering.Browser/paradise-webgpu.js` keeps its plain name either way (so a
  package can hardcode that URL as its default), while `_framework/dotnet.js` becomes
  `dotnet.<hash>.js` and is reachable only through the importmap that `OverrideHtmlAssetPlaceholders`
  injects into `index.html`. So an app's own `main.js` may keep importing `./_framework/dotnet.js`,
  but it MUST keep the `<script type="importmap"></script>` placeholder in its HTML or the stock
  publish breaks. Do not disable fingerprinting to "fix" a missing-module error — check the
  placeholder first.

- [hits: 1] **`Slang.targets` took its slangc RID from `$(RuntimeIdentifier)`, which describes the
  BUILD OUTPUT, not the build machine.** Harmless for desktop builds (the two coincide) until a
  `Microsoft.NET.Sdk.WebAssembly` project imported it: RuntimeIdentifier is `browser-wasm` there, so
  SlangBootstrap went looking for a browser-wasm slangc and the build died in `RestoreSlang` with an
  opaque MSB3073. Fixed 2026-08-13: `_ResolveSlangRid` now always host-detects, with `$(SlangRid)`
  as the explicit override. Rule for build-time native tools: never key their download on the
  project's RuntimeIdentifier.

- [hits: 1] **A `[JSImport]` partial method compiles fine in a plain `net10.0` class library —
  including a `Microsoft.NET.Sdk.Razor` one — as long as `AllowUnsafeBlocks` is on.** No
  `net10.0-browser` TFM and no WebAssembly SDK is needed: the JSImport source generator ships in the
  base SDK and `System.Runtime.InteropServices.JavaScript` is in the `net10.0` reference pack.
  Without `AllowUnsafeBlocks` the error is SYSLIB1074 plus a confusing CS0227. Annotate the public
  types `[SupportedOSPlatform("browser")]` and CA1416 stays quiet. This is what lets
  `Paradise.Rendering.Browser` ship as an ordinary NuGet package whose `wwwroot/` JS rides along as
  a static web asset.

- [hits: 1] **A big struct in a LOCAL kills the wasm runtime — not with an exception, with a process
  abort, and only after a few seconds of steady rendering.** `PbrRenderer.UploadFrameUniforms` built
  its `FrameUniformsGpu` (31008 bytes: 64 lights + 384 shadow matrices) as `var frame = new
  FrameUniformsGpu { … }`. Mono's wasm interpreter runs that fine while interpreting cold, then the
  jiterpreter tries to tier the hot method up and asserts:
  `tiering.c:86 … Unable to run method 'PbrRenderer:UploadFrameUniforms': locals size too big`,
  followed by `ExitStatus` — the whole runtime goes down and the app cannot survive it. Fixed
  2026-08-13 by holding the mirror in a private FIELD and filling it in place through
  `ref var frame = ref _frameUniforms;` (plus `frame = default;` first, because the field persists
  across frames and `AmbientSh[0].w` is the flag that switches the shader onto the SH ambient path —
  a stale one keeps it on after a scene drops SH). Measured bonus: browser frame CPU fell from
  ~2.5 ms to ~0.6 ms, because the method can finally tier up, and desktop loses a 31 KB copy per
  frame. **Rule**: any GPU-uniform mirror over a few KB belongs in a field, never a local, on every
  path a wasm host can reach. `WasmEnableJiterpreter=false` makes the symptom disappear and is NOT
  the fix — it just stops the tier-up that exposes it, at a real throughput cost.

- [hits: 1] **A smoke marker that fires before the failure mode is worse than no marker.**
  `Paradise.Rendering.Browser.Sample` reported `SAMPLE-OK` after 60 frames; the tier-up abort above
  landed at about frame 110, so the acceptance page reported success and *then* the runtime died —
  both scenes had been "verified green" against a build that crashes after two seconds. The DOM
  marker is now gated on 300 frames, and the puppeteer driver has a soak mode (`/tmp/pptr/soak.mjs`
  pattern) that keeps rendering afterwards and fails on `Assertion at`/`ExitStatus`/`locals size too
  big` in the console AND on a frame counter that stops advancing. **Rule**: when choosing a
  frame/tick threshold for a browser smoke test, pick one past wasm tier-up (hundreds of frames, not
  tens), and assert liveness after the marker, not just the marker.

## Packaging / CI

- [hits: 1] **A new packable project under `src/` does not publish until it is named in
  `.github/workflows/publish-nuget.yml`.** The `Pack all packages` step iterates a HAND-MAINTAINED
  `projects=(…)` array — nothing globs `src/`, so a project with a `<PackageId>` and no
  `IsPackable>false` builds, tests and merges perfectly while silently never shipping. PR #182
  (`359c0ec`, the asset pipeline) added four — `Paradise.Assets.Project`, `.Documents`, `.Pipeline`
  and `Paradise.Cli` — and updated nothing; the gap survived the merge because the step's own
  `count -ne ${#projects[@]}` guard only checks the array against itself, never against the repo.
  **Rule**: adding a packable project is a two-file change. To audit the list:
  `for f in src/*/*.csproj; do grep -q "IsPackable>false" "$f" || basename "$f" .csproj; done | sort`
  and diff that against the array. `Paradise.Cli` is the one entry that is not a library — it is a
  `PackAsTool` dotnet tool, which still packs to a single `.nupkg` and pushes on the same run.

## Local SDK vs. pinned Roslyn

- [hits: 1] **`error CS9057: Analyzer assembly … references version '5.9.0.0' of the compiler, which
  is newer than the currently running version '5.3.0.0'` is a LOCAL SDK shortfall, not a broken
  branch.** `src/Directory.Packages.props` pins `Microsoft.CodeAnalysis.CSharp` 5.9.0, so every
  source generator here (`Paradise.Authoring.Generators`, `.BT.Generators`, `.ECS.Generators`) is
  built against a Roslyn newer than the one shipped in SDK 10.0.200 — the compiler refuses to LOAD
  the generator, and every project consuming it fails to build. `global.json` says
  `rollForward: latestMinor`, so CI picks up a newer 10.0.x runner SDK and is fine; a machine with
  only 10.0.200 installed is not. Deleting the generator's `bin/`+`obj/` does NOT help — the rebuild
  reproduces it exactly. **Rule**: when this appears, check `dotnet --list-sdks` before suspecting
  your change; anything that transitively references `Paradise.Authoring` is blocked until a newer
  10.0.x SDK is installed. It is not evidence about the change under test.

## Zio / Paradise.Assets.Pipeline

- [hits: 1] **`CopyFileCross` never reaches an override on a composed filesystem** — it resolves
  both ends through `ComposeFileSystem.ResolvePath` down to the physical/memory filesystem, so a
  `SubFileSystem` subclass that overrides `OpenFileImpl` to record or make writes atomic sees
  nothing. Found 2026-09-02: `ArtifactCache.TryFetch` copied cache hits with `CopyFileCross`
  into the pipeline's `RecordingFileSystem`, and every cache-served texture was silently missing
  from `manifest.json`. **Rule**: when the destination may be a wrapper, open the destination's
  own `OpenFile(Create, Write)` and `CopyTo` — never `CopyFileCross`. Same for `CopyFile`/
  `MoveFile` helpers that take two filesystems. Guarded by
  `ArtifactCacheTests.a_fetch_is_visible_to_a_composed_destination`.

- [hits: 1] **A Coyote spec on `MemoryFileSystem` can pass against a missing lock, because
  MemoryFileSystem tolerates what the OS refuses.** `ArtifactCache.Prepare` without its lock
  passed 300 iterations of "still enabled, one .gitignore line" — two racing preparers both wrote
  the same line and nothing observable broke. What made the test bite was (a) an
  `ExclusiveMarkerFileSystem` that throws `IOException` on a second concurrent writer (Windows'
  sharing rule) and (b) asserting the COUNT of prepares, not the end state. **Rule**: for a
  "happens once" claim, assert the count; for an IO race, inject the filesystem behaviour the
  real OS has. Prove it fails with the guard removed (`-p:TreatWarningsAsErrors=false` to get
  past CA1823 on the now-unused lock field, and check the rewrite actually ran — a failed build
  silently reruns the OLD Coyote binary).

### [hits: 3] A primary-constructor parameter used BOTH in a field initializer and in a method body is CS9124
- The compiler captures it into synthesised state AND stores it, and says so as a warning that
  warnings-as-errors turns fatal. Fix: `private readonly T _x = x;` and read `_x` everywhere
  below. Hit in `History` (twice: `SceneDocument initial`, then `ISceneProvider scene`) and in
  `OperatorDispatcher` (`IOperatorContext context` feeding both `_logger` and every method).
- 2026-09-03/04, editor skeleton.


### [hits: 2] `--solution` is a `dotnet test` switch, NOT a `dotnet build` one, on SDK 10.0.400
- `dotnet build --solution ParadiseEngine.slnx` fails with `MSB1001: Unknown switch`; pass the path
  directly (`dotnet build ParadiseEngine.slnx`). `dotnet test --solution ParadiseEngine.slnx` is
  correct and works — an earlier version of this entry claimed otherwise and was wrong.
- AGENTS.md carried the bad build line until #236 corrected it; the test line was always right.
- 2026-09-03 first hit, corrected 2026-09-04 after a review caught the entry contradicting AGENTS.md.


### [hits: 1] TUnit 1.65: `HasCount()` is obsolete and warnings-as-errors turns it into CS0618; use `.Count().IsEqualTo(n)`
- 2026-09-03, editor skeleton tests.
