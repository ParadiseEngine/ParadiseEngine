# ParadiseEngine — agent guide

A .NET game engine: an ECS, a WebGPU renderer, an unmanaged behavior-tree runtime, an asset
pipeline and the packages a game consumes them through. This file is the canonical guidance for
AI agents; `CLAUDE.md` imports it.

## Build and Test Commands

```bash
# Build all projects
dotnet build ParadiseEngine.slnx

# Run all tests
dotnet test --solution ParadiseEngine.slnx --output normal

# Build/test a single project
dotnet build src/Paradise.BT/Paradise.BT.csproj
dotnet test src/Paradise.BT.Test/Paradise.BT.Test.csproj --output normal

# Run the sample app
dotnet run --project src/Paradise.BT.Sample/Paradise.BT.Sample.csproj
```

AOT compatibility of tree construction and ticking is verified via `Paradise.BT.Sample`, which sets `<PublishAot>true</PublishAot>`. Test projects do not enable AOT so the analyzer-testing harness can use `Reflection.Emit`. The `Paradise.BT` serialization surface (`Serialize`/`Deserialize`) and `Paradise.BLOB`'s `ManagedBlobAssetReference` are not currently covered by an AOT build; adding a dedicated AOT publish-and-run CI job for those paths is a known follow-up.

### Concurrent code gets a Coyote test

**Anything with cross-thread rules — a lock, a shared flag, a queue two threads touch — gets a
systematic test in the matching `*.CoyoteTest` project, not only a stress loop.** Coyote schedules
interleavings deliberately; a stress loop reaches the bad one by luck or not at all. This is not
theoretical: a hand-written race test for the renderer's capture queue passed **three runs out of
three** against code with a real check-then-enqueue defect, while the Coyote test on the same
broken build failed inside 200 iterations.

```bash
# Release, because the `coyote rewrite` target only runs there (needs the coyote CLI)
dotnet build src/Paradise.Rendering.WebGPU.CoyoteTest/... -c Release
dotnet run --project src/Paradise.Rendering.WebGPU.CoyoteTest -c Release -- 200
```

Existing suites: `Paradise.ECS.CoyoteTest`, `Paradise.Rendering.WebGPU.CoyoteTest`,
`Paradise.Assets.Pipeline.CoyoteTest`, `Paradise.Assets.Project.CoyoteTest`, `Paradise.Cli.CoyoteTest`,
`Paradise.Ui.ImGui.CoyoteTest`, `Paradise.Features.CoyoteTest`.

A fourth thing, learned from the asset watcher: **lock on an `object`, not on
`System.Threading.Lock`, in anything a Coyote suite covers.** Coyote (1.7.11) rewrites
`Monitor.Enter`/`Exit` and does not intercept `Lock.EnterScope`, so with the newer type it cannot
control the lock — every iteration reports the wait as a potential hang, and silencing that would
only hide the fact that the interleavings around that lock are never explored. The newer type is
worth having where a lock is hot; it is not worth a suite that cannot see it.

Three things worth knowing before writing one:

- **Extract the managed part first.** Coyote schedules `Task`, `lock` and concurrent collections —
  it cannot see inside a native call. The renderer's capture path is mostly Dawn
  (`OnSubmittedWorkSync`, `MapSync`, `RequestAdapterSync`), so the queue, its flag and its drain
  were pulled into `CaptureQueue`, which has no native calls at all. Testability was the reason,
  and it is usually the reason such an extraction is worth it.
- **Await joins; do not block on them.** `Task.WaitAll` parks a thread, which Coyote cannot
  distinguish from a deadlock — it reports every such test as a potential hang even when the code
  is correct. Making the tests `async` keeps hang detection ON and meaningful, instead of switching
  it off with `WithPotentialDeadlocksReportedAsBugs(false)`.
- **Prove the test fails without the fix.** Reintroduce the defect, watch it fail, restore. A
  concurrency test that has never failed is a guard nobody has checked the lock on.

These projects are deliberately NOT `IsTestProject` — they are standalone runners with their own
`Main`, so `dotnet test` skips them and they must be run explicitly.

## Project Overview

Paradise Engine is a .NET behavior tree runtime library inspired by EntitiesBT, with a companion binary blob serialization library. It targets `net10.0`, uses C# 14, and is NativeAOT/trimming compatible.

### Coordinate convention

The engine and its data contract are **right-handed: Y-up, −Z forward, +X right** (Godot / glTF
standard), in meters, with **column-major** matrices. This matches what the editor tools
(`ParadiseGodotEditor`) export — the exporter writes Godot values verbatim, with no handedness
conversion. Any future scene/navmesh/level loader must consume right-handed data directly (no
Z-mirror). The engine core (`Paradise.ECS`, `Paradise.Rendering`) is otherwise coordinate-agnostic;
handedness only enters where transforms, camera/projection matrices, or navmesh geometry are built.

### Monorepo Layout

- `src/Paradise.BLOB` — Standalone unmanaged binary blob builder (BlobArray, BlobString, BlobPtr, builders). No external dependencies. Target: `net10.0`.
- `src/Paradise.Features` — Engine-wide feature configuration: the features a build declares and the switches that turn each on or off at runtime. No package dependencies at all, because the renderer and the ECS both reference it.
- `src/Paradise.Features.Toml` — Reads `engine.toml` into that. A separate assembly so Tomlyn stays out of the ECS's dependency closure.
- `src/Paradise.BT` — Behavior tree runtime built on top of Paradise.BLOB. Target: `net10.0`.
- `src/Paradise.BT.Sample` — Console sample demonstrating tree construction, blackboard usage, and ticking.
- `src/Paradise.BT.Test` / `src/Paradise.BLOB.Test` — TUnit test suites.
- `src/Directory.Build.props` — Shared build properties (C# 14, nullable, unsafe, warnings-as-errors).
- `src/Directory.Packages.props` — Centralized NuGet package versions.
- `ParadiseEngine.slnx` — Solution file (modern slnx format).

## Architecture

### Behavior Tree Pipeline

1. **Authoring** — generated builder classes (from `[Builder]` via `BTreeNodeGenerator`) or the raw generic wrappers (`LeafNode<T>` / `DecoratorNode<T>` / `CompositeNode<T>`) compose a `BTreeNode` graph (`Paradise.BT.Builder`).
2. **Compilation** — `BTreeNode.Build()` validates each builder's arity against its node's `[Builder]` cardinality (Leaf = 0, Decorator = 1; no attribute claims Leaf) and flattens straight into a `BehaviorTreeLayout`: one shared native blob of end indices, a GUID table, per-node data offsets (natural alignment, capped at 16) and authored defaults. `BehaviorTrees.Compile<TTree>()` does the same from a tree TYPE and returns a typed `BehaviorTreeLayout<TTree>`. A thousand agents share one layout; there is no serialization — trees compile from code.
3. **Instantiation** — an instance is two caller-owned buffers over the layout: `BehaviorTreeRef` (a ref struct view) for arbitrary spans, or `FixedBehaviorTree<TTree, TStates, TData>` for inline-in-a-component storage. The blackboard is passed per `Tick(bb)` call, so `ref struct` (generated) blackboards work.
4. **Execution** — `VirtualMachine.Tick()` dispatches each node by its GUID through `NodeTypeRegistry` and ticks it through its bytes. Registration is emitted per assembly by the generator as a module initializer.
5. **Type safety** — the binding generator stamps each generated blackboard `IBlackboardFor<TTree>`; the typed layout/ref and `FixedBehaviorTree` only accept that tree's blackboard, so a mismatch is a compile error.

### Key Abstractions

- **`INode`** — The core node contract: unmanaged struct with generic `Tick<TBehaviorTree, TBlackboard>(int index, blob, bb)`; optional `static virtual Reset`. Identity is `[Guid]`.
- **`IBehaviorTree` / `BehaviorTreeRef`** — The instance view over the shared `LayoutBlob` plus caller-owned spans (states + runtime data). Data is reached by `ref byte`, so buffers may be managed arrays or native/chunk memory.
- **`NodeTypeRegistry`** — Process-wide GUID → invoker table; the GUID is the whole identity.
- **`IBlackboard`** — Three members (`HasData`/`GetData`/`SetData`), no ref returns, which is what makes read/write intent statically checkable by the generators.
- **`NodeState`** — Flags enum (`None`, `Success`, `Failure`, `Running`); `None` means "never ticked / reset".

### Custom Node Pattern

Implement `INode` on an unmanaged struct, tag with `[Guid("...")]` (and `[Builder]` for a generated builder class), then compose it via its builder or `new LeafNode<MyNode>(...)`:

```csharp
[Guid("...")]
public struct MyNode : INode
{
    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        // access runtime/default data via blob.GetNodeData<MyNode>(index)
        // access shared state via bb.GetData<T>()
        return NodeState.Success;
    }
}
```

### Paradise.BLOB

Low-level unmanaged binary blob library backing the BT layout. Key types: `BlobArray<T>`, `BlobString<TEncoding>`, `BlobPtr<T>`, `ManagedBlobAssetReference<T>`. Builders (`ValueBuilder`, `StructBuilder`, `ArrayBuilder`, `TreeBuilder`, `SortedArrayBuilder`) produce pinned memory blocks.

### Runtime global illumination: probes over a compute ray tracer

**Indirect light is a probe volume, updated every frame by rays traced in compute against the
scene's own geometry — no bake, no hardware ray tracing, WebGPU only.** `PbrScene.Gi.Enabled`
turns it on; the volume fits the static scene unless `PbrGi.Volume` authors one. Three layers,
each testable on its own:

- **`Paradise.Geometry`** builds the hierarchy: `TriangleBvh.Build` (binned SAH, collapsed to
  8-wide nodes with 8-bit quantized child bounds — `BvhNode`, 96 bytes, mirrored byte for byte by
  `Common/bvh.slang`) and `BvhTraversal.ClosestHit`, the CPU twin of the shader walk that the
  tests hold the builder against. Decoded child boxes are always conservative; a test proves it.
- **`TraceScene`** (in `Paradise.Rendering.Pbr`) is the scene as the tracer sees it: ONE merged
  buffer per kind — nodes, triangles, vertices — with every primitive's hierarchy rebased to
  absolute indices at `UploadPrimitive` (there is no bindless, so a mesh cannot be a buffer of its
  own), plus a per-frame top-level hierarchy over the opaque `PbrGiMode.Static` instances whose
  instance table is laid out in leaf order. It is rebuilt only in frames something traces.
- **`ProbeGiFeature`** runs four compute passes at `RenderPassEvent.GlobalIllumination`: trace
  (`probeTrace.slang`, hits shaded with the frame's lights and shadow maps, the material's
  factors, and LAST frame's probes — the infinite bounce; misses take the sky ambient), two
  blends into ping-ponged octahedral atlases (`probeBlend.slang`: irradiance at 8×8 per probe,
  distance moments at 14×14, each tile with a 1-texel wrap border so bilinear reads cross edges),
  and relocation/classification for the next frame (`probeUpdate.slang`). `pbrCore.slang` replaces
  the sky ambient with `sampleProbeIrradiance` for surfaces inside the volume (Chebyshev
  visibility keeps a probe behind a wall from leaking through it) unless the draw's GI mode is
  disabled. `RayTracedAoFeature` is the tracer's proving ground: a picture that must darken.

Things that bit, so they are rules:

- **slangc keeps every global an included file declares, referenced or not.** A compute shader
  that includes `pbrCore.slang` inherits the whole raster layout and collides with its own group
  0. `Common/lighting.slang` holds exactly what shading a hit needs (frame UBO, shadow array and
  sampler at group 1 bindings 0–2, attenuation and shadow lookup); `uniforms.slang` adds the
  raster-only bindings on top. Check a new program's bind groups in
  `obj/…/shaders/<name>.reflection.json` before trusting them.
- **A compute pass fails silently when a layout entry is not visible to it.** Dawn reports the
  pipeline error asynchronously and the dispatch is dropped; the loader's name-based overrides
  (`shadowTexture`, `prepassDepthTexture`, …) therefore take the file's default visibility. Prove
  a compute pass with a picture that must change, never with "it ran".
- **The frame graph knows compute:** `AddComputePass`, `GraphBinding.StorageTexture` (a WRITE
  edge), `ImportBuffer` + `GraphBinding.TrackedBuffer(…, write:)` for tracked buffers (named
  apart from the raw `Buffer` so a tracked buffer cannot lose its edge by overload). A trace whose
  only output is a private hit buffer is culled the moment nothing binds that buffer for
  reading — the same switch-off rule textures have. A pass reading last frame's atlas declares a
  plain read of a resource nothing writes this frame, which the graph allows.
- **The probes and the sky share one irradiance convention: E/π.** A probe texel is the
  cosine-weighted mean of its rays' radiance, which is what the SH sky ambient already is, so a
  scene under a flat sky renders the same with the probes on or off (a test pins this, within the
  darkening the probes correctly see below the horizon). Direct light at a hit follows the
  raster's non-physical convention (no 1/π); the sky term carries the exposure, so exposure is
  applied nowhere else on the probe path.
- **The depth + normal pre-pass declares the WHOLE vertex stream** even though it reads two
  attributes: the reflected stride comes from the struct, and a position-only struct once
  sampled interleaved normals as positions for as long as SSAO existed.
- **Profile with the sample's `--bench` in a profiling build (`dotnet build
  -p:ParadiseProfiling=true`, which defines `PARADISE_PROFILING` in every project), and trust the
  frame total, not the per-pass rows, on Apple GPUs.** The timestamp plumbing, the CPU phase
  laps and the bench compile only then; the members stay in the API and report nothing
  otherwise. `WebGpuRenderer.PassTimingEnabled` + `ReadPassTimings` give per-pass timestamp
  pairs (names from `FrameGraph.LivePassNames`), but Apple GPUs run passes concurrently and a
  timestamp pair measures wall time while other work is in flight — every bloom mip "took" 3 ms
  beside a compute trace, for a 5 ms frame. The bench also prints the GPU idle-to-idle frame time
  (submit, then wait); attribute cost by toggling features (`--no-gi`, `--rtao`, `--no-bloom`,
  `--gi-rays N`, `--gi-max-probes N`, `--gi-probes-per-frame N`). Measured on an Apple M-series at
  1280×960 in the Cornell room: base 2.1 ms (bloom 0.8), probes +2.3 ms at 3072 probes × 128 rays
  (linear in rays), RT-AO +2.6 ms at half resolution with 8 rays. Two things that paid: staging a
  probe's rays and directions in workgroup memory once per blend workgroup (halved the blend), and
  an early-out any-hit walk plus half resolution for RT-AO (9.5 → 2.6 ms). One that did not:
  nearest-first child ordering in the traversal (+0.3 ms; the sort outweighed the skipped nodes).

### A feature is switched by engine configuration, not by a flag of its own

**Anything a build can turn off declares a `FeatureDefinition` and asks `IFeatureSwitches`
whether it is on — the renderer's features, an ECS schedule's systems, and whatever a game adds
next.** `Paradise.Features` holds that: `FeatureId` (a validated dotted name — `rendering.bloom`,
`gameplay.weather`), the declaration that gives it a default and a description, and
`FeatureSwitches`, the one object per process that answers. `TomlEngineConfiguration.Read` parses
`engine.toml`; `FeatureOverrides.Parse` reads a `--features +a,-b` flag or `PARADISE_FEATURES`.
Layers merge nearest-last: declarations, then the file, then the environment, then the command
line. `dotnet run --project src/Paradise.Rendering.Sample -- --list-features` prints what a build
has.

A feature is also configured, not only switched. The file's second section carries a settings
table per feature, and `switches.SettingsFor(id).Read(GameJson.Default.WeatherSettings)` binds it
to the caller's own record. Nothing engine-side uses it: an engine feature's parameters are
scene-authored, and this exists for the game feature the engine has never heard of.

```toml
# engine.toml
[features]
"rendering.globalIllumination" = false   # the integrated GPU cannot afford the probe trace
"game.weather" = true

[settings."game.weather"]
intensity = 0.6
windMetresPerSecond = 3.5
```

**The reader is a second assembly, `Paradise.Features.Toml`.** Reading TOML needs Tomlyn, and
`Paradise.Features` has no package references because `Paradise.ECS` — which references nothing
else at all — references it; a TOML parser in the ECS's closure is a cost every consumer of the
ECS pays for a file only a host reads. Same shape as the logging rule below: the abstraction is
dependency-free, the concrete reader is a package the host picks. What crosses the seam is
`EngineConfiguration`, a bag of names and values that depends on nothing.

The assembly has **no package references at all**, deliberately: `Paradise.ECS`, which otherwise
references nothing, references this. Its one diagnostic — a configured name no declaration claims,
`FeatureSwitches.Unknown` — is reported as data rather than logged, so it needs no logging
abstraction to say it. It is called `Paradise.Features` rather than `Paradise.Configuration`
because a namespace of the latter name is in scope for every file under `Paradise.*` and then beats
an imported type called `Configuration` — every Coyote suite here says `Configuration.Create()`
meaning Microsoft.Coyote's, and all six stopped compiling.

Eleven things that are not obvious:

- **A switch and a scene setting are different questions, and both have to say yes.** The switch is
  the platform's answer ("this build does not do probe GI"), applied once from configuration; a
  scene's own `Enabled` (`PbrGi`, `PbrBloom`, …) is the content's answer ("this level uses it"),
  authored per level. Collapsing them either lets a level override a platform decision or forces
  the platform decision to be re-made in every level. They behave differently too, and a test pins
  it: a scene that wants no bloom leaves the chain DECLARED and the graph culls it; a switch that
  is off means the feature never runs and there is nothing to cull.
- **Order does not matter, and that is load-bearing.** The config file is read before the renderer
  exists, so an override lands on a name nothing has declared yet. It is kept by NAME and still
  wins when the declaration arrives — and a name no declaration ever claims stays in `Unknown`
  instead of vanishing, because a typo in a config file must not read as a feature that is off.
- **A feature that leaves state behind must implement `IRenderFeature.OnEnabledChanged`.** Being
  switched off is not the same as declaring no passes: the shadow plan, the pre-pass's SSAO
  uniforms and the probe volume are all read by the SCENE every frame whether or not the feature
  that owns them ran. Left alone they repeat the last enabled frame — a light keeps sampling a
  shadow layer nothing fills, ambient is multiplied by a black occlusion texture the flags still
  call real. The pipeline calls the hook on the transition only, including once at `Add` when
  configuration already said off. Each of the three is guarded by a test that fails without it.
- **Adding a feature touches no renderer.** `PbrBuiltInFeatures` is the whole list of engine
  features and the only place a new one goes; a game calls `RenderPipeline.Add(feature, order)`
  at a `PbrFeatureOrder` slot and needs nothing here. Order is a spaced integer for the same
  reason `RenderPassEvent`'s is — a game feature that must publish before the scene reads it
  cannot say so with a list position when the engine does all the adding. `PbrRenderer` holds no
  feature reference at all: what its own API forwards to it looks up through `Pipeline.Find<T>()`,
  on cold paths only.
- **A feature that fills a buffer while RECORDING uploads it in `BeforeSubmit`.** A recorder runs
  inside the compile, so the shadow pass's caster ring has nothing in it when `Setup` returns and
  no moment left after the submit. That upload used to be a line in `PbrRenderer.RenderFrame`
  reaching into `ShadowFeature`, which meant the frame loop knew that one built-in stages draws
  and a GAME's feature with the same need could not be uploaded at all. The hook is on the
  interface so both are served by the same call; the pass-matrix baseline goes red if the pipeline
  stops making it.
- **The feature name is ONE key, quoted.** TOML reads `rendering.bloom = false` as a table
  `rendering` holding `bloom`, and under `[settings]` that nesting cannot be told from the settings
  themselves — `[settings.game.weather]` is either the feature `game.weather` or the feature `game`
  with a setting `weather`. One rule for both sections, so neither is ambiguous: quote the name. A
  table where a feature's state belongs is refused with the quoted form in the message.
- **The settings PAYLOAD is JSON even though the file is TOML.** `Paradise.Features` holds no
  format reader, so the payload has to be text it can bind with the BCL alone, and
  source-generated System.Text.Json is the BCL's only AOT- and trim-clean typed binding. Binding
  straight from TOML was the alternative and it was MEASURED, not assumed: Tomlyn 2.10's
  source-generated `TomlSerializer` matches the C# property name exactly on the way in
  (`Intensity = 0.6` binds, `intensity = 0.6` silently reads as the default) while emitting the
  lower-case key on the way out, so a table → text → object round trip loses every value, and a
  direct bind would make a hand-edited file spell its keys `WindMetresPerSecond` beside camelCase
  feature names. Nothing a person writes is JSON; `FeatureSettings.Json` is named for what it
  hands back, and the conversion runs once per configured feature at startup.
- **A settings type's properties are `get; set;`, never `init`.** An `init` accessor makes
  System.Text.Json build the object WITHOUT running the parameterless constructor, so every
  property the file leaves out reads as `default` — 0, not the `= 1f` the initializer says — with
  no error either way. It is the accessor and not `record` vs `class`; all four combinations were
  probed. The same class of silence sits next to it: a source-generated `JsonSerializerContext`
  matches the C# property name EXACTLY unless told otherwise, so a camelCase file binds nothing at
  all. Both are pinned by `FeatureSettingsTests`.
- **Settings merge WHOLESALE between layers, not deep.** A deep merge reads well in the two-file
  case and has no answer for "which layer owns element 3" the moment an array is involved; a later
  file that means to change one field writes the table it wants.
- **`PbrRenderer` and `RenderPipeline` take the switchboard as a REQUIRED argument.** It was a
  defaulted last parameter for about a day, and that is a host getting a private switchboard, every
  feature at its declared default, and a config file that reached nothing — no error, and a frame
  that still renders. A caller that configures nothing writes `new FeatureSwitches()` and has said
  so.
- **Writes are serialized and `Changed` is raised inside that same critical section.** Deciding
  "did this change?" and announcing it is a check-then-act, and a subscriber ACTS on the
  announcement; with two writers the last announcement could otherwise contradict the state
  everyone now reads, leaving a feature switched on with its state retracted.
  `Paradise.Features.CoyoteTest` pins it — three of its five specs fail within 200 iterations
  against the unlocked version.

### Diagnostics go through `ILogger`, never `Console`

**An engine library takes an `ILogger` and references `Microsoft.Extensions.Logging.Abstractions`
and nothing else.** Which sink a game logs to is the host's decision, the same way the mount is
(above). `Paradise.Diagnostics` is one sink, used by `Paradise.Cli.Host`; adding a *provider* —
ZLogger, Serilog, `Microsoft.Extensions.Logging.Console` — to a `Paradise.*` library decides for
every host at once and is the mistake the rule exists to prevent.

Three things that are not obvious, all of which the build will teach you the hard way:

- **Use `[LoggerMessage]`, not `logger.LogInformation(...)`.** The generator ships inside the
  Abstractions package and emits the `IsEnabled` check BEFORE touching arguments, so a disabled
  level costs no boxing and no template parse. It needs a `partial` class, and its `ILogger`
  parameter **cannot be nullable** — the generated body calls `IsEnabled` unguarded, so `ILogger?`
  fails with CS8602 inside generated code. Carry `NullLogger.Instance` instead.
- **Log a `UPath` as an argument, never a pre-rendered string.** The reader does not know what its
  filesystem is mounted over and must not guess; the host does, and installs a renderer
  (`ParadiseConsoleOptions.RenderValue`). A `Display`-style helper that shortens a path inside a
  library is one host's preference in the layer that cannot know it.
- **Thread safety is the sink's job.** Dawn, Noesis and SDL all call back on threads the engine did
  not create, and `ILogger` promises no affinity.

Program output is not a diagnostic: `Verbs` printing `verify: 3 error(s)` and
`Paradise.Authoring.SchemaDump` writing its dump keep `Console.WriteLine`.

### Everything that reads content takes an `IFileSystem`, not a path

**A reader takes a Zio `IFileSystem` and a `UPath`; the HOST decides what that is mounted over.**
This is one vocabulary across both halves of the engine — the asset pipeline was already on Zio
(`Paradise.Assets.Pipeline`, `Paradise.Assets.Documents`), and the runtime readers
(`AuthoredDocument.Load`, `Paradise.Ui.Noesis`'s XAML/texture/font providers) now are too. A
shipped build can mount an archive, the editor mounts its play tree, a test mounts memory.

Three things follow, and each replaced code somebody had written by hand:

- **Do not translate separators.** A `UPath` is `/`-separated on every platform, which is exactly
  how the asset contract spells a field, so a field combines onto its root verbatim. Every
  `Replace('/', Path.DirectorySeparatorChar)` in a reader is a sign the mount was not used.
- **Containment is the mount's, not a check you write.** Combining a `..` that climbs past the
  root throws, and an absolute uri resolves INSIDE the mount rather than escaping it — so
  untrusted content (a GLB's image uris) is confined by a `SubFileSystem` over the file's own
  directory. Wrap the refusal only to name what asked for it; do not re-implement the rule.
- **A test mounts memory rather than a temp directory.** `MemoryFileSystem` needs no cleanup, so
  no fixture survives a test that throws before its `finally`. `SubFileSystem` over the test's
  output directory is what makes fixture paths read like a shipped tree.

**The exception, and why it is one:** `Paradise.Audio.Wwise` keeps host paths. The native loader
opens the bank file itself, so no mount can back it, and an abstraction that lies at that layer is
worse than none. When a reader genuinely cannot go through the mount, say so where it does not.

Two things a wrapper must respect, both learned the hard way (see `.claude/lessons.md`):
`CopyFileCross` resolves through a composed filesystem down to the physical one and never reaches
a subclass's `OpenFileImpl`, and a Coyote spec over `MemoryFileSystem` can pass against a missing
lock because memory tolerates what the OS refuses.

### An asset reference resolves by GUID, never by path

**An `AssetReference` is `{ guid, path }`, and only the guid names the asset.** Resolution goes
through `AssetIndex` — the one ordinal scan of `assets/`, holding both what exists and which
asset carries which guid — which every consumer (build, bake, prefab resolution, verify, `--fix`)
shares. It is deliberately ONE object: the file set and the guid map come from the same walk, so
splitting them only invites passing a mismatched pair. Pass an `AssetReference` wherever a
reference travels; a raw path string as a parameter is the shape this rule exists to keep out.

The path half is carried for the diff and the grep, and it is allowed to be wrong. A rename in
Finder or with `git mv` leaves it stale while the identity is intact, so `verify` reports that as
a **warning** (with `--fix` to catch it up) and only a guid no asset carries is an error.
`paradise assets mv` still rewrites eagerly; that keeps the tree tidy, it is not what keeps it
working. Two ways to reintroduce the bug: resolving a reference with `assetsRoot / reference.Path`
(use `AssetIndex.AssetOf`), and keying a cache or a cycle check on `reference.Path` (key on
`reference.Guid`, and carry the path only to phrase the message).

**A GLB ships nothing; documents name its parts and the build cooks them.** `GlbImporter.Import`
writes no output. A `.mesh`, `.skinnedmesh`, `.skeleton` or `.anim` under `assets/` is a
`MeshReferenceDocument` — `{ source = { guid, path }, slot, name, index, hash, skeleton }` — and
`MeshImporter`/`SkinnedMeshImporter`/`AnimationImporter` cook the named part of the GLB (`GltfCook`)
to the file at the document's own path: the mesh to a `Paradise.BLOB` blob (`Paradise.Assets.Mesh`,
one aligned native copy, magic and version first), the skeleton and clips to **ozz-animation
archives**. **A skinned mesh is its own kind.** The extractor mints a `.skinnedmesh` for a GLB with
a skin and a `.mesh` otherwise — the GLB decides, never the author — and the skinned document
names the `.skeleton` minted beside it; MeshBlob v3's skin carries that skeleton's BUILT path, so
the runtime opens a mesh and reads where its rig is. A game's authoring accepts `.skinnedmesh` where
a rig is required and `.mesh` where one is not, so the picker cannot offer the wrong kind. A `.mesh`
over a rigged GLB, or a `.skinnedmesh` over a rigid one, is a build error naming the watcher; a GLB
that gains or loses its rig has its stale document replaced under a fresh identity.
A clip is found by name, then by the hash
of its channels, then by index. **A built document names assets where the build PUT them.**
`ImportContext.BuiltPath` resolves a reference and asks the referenced asset's own importer
(`IAssetImporter.BuiltPath`, default: the asset's own path) — the importer that writes a texture
as KTX2 is the one that knows it does. So a texture reference bakes to its `.ktx2`, prefabs and
configs to the profile's extension, and mesh, skeleton, clip, material, audio and binary references
to their own path (a built `.material` keeps its suffix and carries TOML or JSON by profile;
`ExportDocumentReader.ReadMaterial` tells them apart by the first character). A GLB ships nothing,
so a reference to one is a build ERROR naming the `.mesh` document to reference instead: an authored
document references the document the watcher minted, the way it references a `.skeleton` or an
`.anim`, and the GLB is source the way a `.png` is source to its `.ktx2`. Both the prefab bake and
the material bake go through it, so a runtime opens the path a built document spells and never
derives one by convention, and a game's own importer answers for its own kinds.

**Animation is a managed port of ozz-animation, pinned to its 0.17 archive format.**
`Paradise.Animation` reads and writes `ozz-skeleton` (v2) and `ozz-animation` (v7) archives
(`OzzArchive.ReadSkeleton/WriteSkeleton/ReadAnimation/WriteAnimation`) and carries both halves in
C#: the runtime and the offline side (`SkeletonBuilder`, `AnimationBuilder`, `AnimationOptimizer`,
`ClipConverter`). No native code anywhere, so NativeAOT and browser hosts get it for free. The
RUNTIME is unmanaged: `SkeletonBlob`, `AnimationBlob` and `SamplingContext` are `Paradise.BLOB`
layouts opened as `NativeBlobAssetReference<T>` (one native allocation each, at load), and
`SamplingContext.Sample(ref clip, ratio, poses)` plus `LocalToModel.Compute(ref skeleton, ...)`
allocate nothing — reach every blob through a `ref`, never a copy (see the BLOB README). The
sampler keeps ozz's structure-of-arrays half: keys are decoded and interpolated four tracks per
`Vector128` lane; the cursor walk is scalar on purpose (its search hits within an entry or two,
so a vectorized `IndexOf` costs more than it saves), and lanes leave a vector through a stack
store, never `GetElement` with a variable index (a software path). On Enemy it matches native
ozz per frame. `AnimationPlayer` is the per-character layer on top: current and outgoing clip,
time, rate, loop or clamp, cross-fade, `Advance(dt)` then `Evaluate()` into `LocalPose` and
`ModelMatrices`; its cursors, pose sets and matrices are ONE blob (`AnimationPlayerState`), so a
character is one native allocation and the class only holds the clip and skeleton references.
`SkinningPalette.Compute` turns those plus a mesh's skin (as spans) into the GPU palette. A game host (ShiningPie's `ActorAnimator`) should hold a player per actor and
nothing more. Poses travel as `JointPoses`, a native blob in ozz's structure-of-arrays layout
(groups of four joints, one `Vector128` per component with a joint per lane): the sampler writes
it without a transpose, `JointPoses.Blend` and the hierarchy walk take four joints per
instruction, and the indexer gathers one `JointPose` for per-joint code (attachments, tests),
which is not the hot path. The
archive is the only persisted format; blobs exist in memory only. The offline builders are
managed (lists, sorting) because they run in the cook, not the player, and hand back native blobs.
Inside a blob's hot loop take `field.ToSpan()` ONCE and index the span: the `BlobArray` indexer
re-derives its pointer and bounds-checks on every access, and doing that per key doubled the
sampler's cost. `src/Paradise.Animation.Benchmarks` (BenchmarkDotNet) measures one frame of one
character for the blob runtime, the frozen managed-class copy it replaced, the glTF reference
sampler, and ozz's own C++ when `PARADISE_OZZ_NATIVE` points at the spike's shim library;
`PARADISE_BENCHMARK_GLB` swaps the procedural rig for a real character. The contract is held as bytes:
`OzzParityTests` regenerates a procedural rig and checks this builder writes exactly what ozz's
C++ builder wrote (`Paradise.Animation.Test/Fixtures`), so a change to key sorting, quantization
or i-frames that still "works" fails there. The skeleton is the GLB's WHOLE node tree in ozz's
depth-first order (parents first, siblings by ascending glTF node index); skins, clips and draws
address joints by that index, and the mesh blob carries its own skin (palette slot → joint +
inverse bind) so one skeleton can drive many meshes. Unanimated joints hold the REST pose, not
identity — ozz's builder pads an empty track with identity, `ClipConverter` fills rest first.
STEP channels are baked into held keys and rotation arcs wider than 15° get slerped keys inserted
(ozz only lerps — normalized lerp for rotations — where glTF means slerp, and a 90° arc puts the
two a degree apart mid-way). A clip is then lossless but for ozz's 16-bit quantization unless the GLB's sidecar sets `[glb] optimize = { tolerance, distance }`
(`AnimationOptimizer`; ozz's defaults are 1 mm at 10 cm). The reference sampler that plays the
source glTF (`GltfAnimationRig`) lives in `Paradise.Assets.Pipeline.Test` only, as the golden
test's oracle. The documents are tool-owned: the watcher mints
them for any GLB with geometry, and `extract` overwrites one that disagrees with the GLB without a
conflict rule — but never one that names ANOTHER GLB. Materials, textures and the prefab are
authored the moment they exist, so only the `extract` verb writes them, and the GLB's `[glb]`
sidecar domain records a material's or image's entry with a fingerprint of BOTH sides (the GLB
side is what the GLB would extract to now; a material's is over the glTF-expressible subset only)
so `extract` tells a re-export from an edit and refuses when both moved. KTX2 is never authored:
`verify` errors on one under `assets/`. Two traps: reach every `BlobArray`/`BlobString` through a
mutable `ref` (a readonly reference copies the header and its relative offset points at the
stack), and read a sidecar domain's inline tables through both `CanonicalTomlTable` and
`CanonicalInlineTable` (the reader hands a root-level inline table back as a plain one).

**The sidecar names the importer; nothing walks the chain except `ImporterChain`.** `Claims` is
the one claim point — cheap, a path and at most a header — and the maintainer records the answer
when it mints the sidecar. `Import` keeps its extension guard only as a defence: a hand-edited
`importer` line can name it for anything, and the build reports a decline as an error rather than
skipping the asset. A recorded name is never overwritten by the tooling, and a name the chain lacks
is never silently replaced by a claim — both are what recording it is for. Decided at mint, not at
first build: a build that edits committed sidecars is the dirty-tree failure the recorded hash
already taught. Two ways to reintroduce the old search: walking `importers` anywhere but
`ImporterChain`, and a `Claims` that reads more than a header.

**"Who references X" is `ReferenceGraph`, and it is derived.** Built from `AssetIndex` plus what
each importer declares through `IAssetImporter.References` (a prefab's document, a mesh's `[mesh]`
sidecar entries — and a game's own kind, with no format list anywhere in the pipeline); never persisted, and a DOCUMENT's
reference list never goes in a sidecar (a second copy of the document, kept in sync by a watcher that
may not be running, dirtying two files per edit). A mesh container's references DO live in its
sidecar (`MeshImportSettings`), because that is derived data the tooling resolves from bytes it
cannot author — the container is read, never written, so FBX and GLB get one mechanism; the uri
is rewritten only where the format allows, and only for the DCC's benefit. Nodes are guids; an edge into an identity nothing
carries is KEPT with its path, because that is the moment someone asks who pointed there. `mv`
rewrites `DependentsOf` the moved guids (plus what the graph lists as `Unreadable`, walked the old
way), `rm` refuses on non-empty dependents unless forced and never nulls a slot, and the watcher
follows a carried identity's dependents after a rename — skipping one still inside its debounce
and retrying it next drain. All of them go through `ReferenceChain` and `IAssetImporter.Rewrite`;
a verb that branches on an asset's extension is the shape this rule exists to keep out. A
reconcile at build time passes `RewriteSources = false`: sidecars only, never a path or a uri
moved under the author's feet. A container uri with no entry recorded is the one path-only reference
left: it is in `PathOnly`, and a move follows it only when the move touched it, and otherwise warns.

## Code Style

Enforced via `.editorconfig` with warnings-as-errors:
- **Naming**: private/internal fields `_camelCase`, statics `s_camelCase`, constants `PascalCase`, public fields/properties `PascalCase`
- **Layout**: Allman braces, 4-space indent, file-scoped namespaces, LF line endings
- **Types**: Prefer language keywords (`int` not `Int32`), avoid `this.` qualification
- **Performance**: Struct-based nodes, `ref` parameters throughout, zero-allocation design, `System.Runtime.CompilerServices.Unsafe` for low-level ops
- **Collections**: prefer empty over null. A method whose result is a list, set, or a record of them returns an empty one (a shared static like `AssetReferences.None` where the type is a class); null is reserved for "there is no such thing" on a single object, never for "nothing in it". A caller iterates an empty result with no branch; a null forces one at every call site and is the shape of the next `NullReferenceException`.
- **Comments**: Code explains itself; comments explain why. Prefer a name, a type, a small method, or a guard over a comment that says what the code does, and restructure before commenting. A comment is for what code cannot say: a constraint, a decision and its rejected alternative, a failure mode someone would reintroduce, a cross-repo or cross-language contract. XML `<summary>` is one sentence; `<remarks>` only when it carries such a why. Delete comments that narrate control flow or restate the next line.

## Git conventions

- Feature branches off `main`; PRs assigned to quabug; squash-merge, matching the history style.
- A PR that fixes an issue carries `Closes #NNN` (one line per issue) at the top of its body,
  and the commit message says it too, so merging closes the issue. Use `Towards #NNN` only for
  deliberately partial work, and when a second fix joins an existing PR, add its `Closes` line.
- Never commit or push without being asked.

## SDK

Requires .NET SDK 10.0.400+ (specified in `global.json` with `rollForward: latestMinor`). The
floor is the compiler, not a preference: the Roslyn analyzers this repo builds against are
compiled for 5.9.0.0, and an older SDK's `csc` refuses to load them with CS9057. `latestMinor`
rolls forward, never back, so an older SDK does not satisfy this and the build stops with a
version message rather than an analyzer one.
