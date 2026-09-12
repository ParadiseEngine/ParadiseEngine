# Hosting and feature configuration

`Paradise.Hosting` runs window, simulation and presentation loops through `IHostApplication`.
`Paradise.Hosting` loads layered configuration; `Paradise.Hosting.Desktop` selects SDL/offscreen surfaces. Games
provide owner-thread factories and per-tick/per-frame callbacks; see `src/Paradise.Hosting/README.md`.
Presentation must finish and release snapshots before the simulation disposes worlds. A timed-out
worker retains its borrowed resources until it exits; host failure must propagate to the process.
Use `SnapshotStream<T>` to publish and recycle snapshots: the newest world is reserved as the next
simulation read world until another is published, even if the renderer returns it early.

Every switchable subsystem declares a `FeatureDefinition` and reads `IFeatureSwitches`.
`Paradise.Features` has no package dependencies; `Paradise.Features.Toml` isolates Tomlyn from
ECS consumers. Keep this name: `Paradise.Configuration` would shadow Coyote's `Configuration`.

Overrides merge in order: declarations, `engine.toml`, environment (`PARADISE_FEATURES`), then
CLI (`--features +a,-b`). `FeatureId` is a validated dotted name. Overrides may precede declaration;
retain unknown names in `FeatureSwitches.Unknown` so hosts can report typos.

```toml
[[features]]
name = "rendering.globalIllumination"
enabled = false

[[features]]
name = "game.weather"
enabled = true
intensity = 0.6
windMetresPerSecond = 3.5
```

- Use one `[[features]]` entry per feature; `name` and optional `enabled` are reserved. Reject
  duplicates. Other keys are settings; later layers replace the entire settings table.
- Carry settings as text and bind through the producing reader (`FeatureSettingsToml.Read<T>`)
  with the caller's source-generated `TomlSerializerContext`. Do not convert to JSON or reflect.
- Settings use `get; set;` and a context naming policy. `init` can lose omitted-value initializers;
  without `PropertyNamingPolicy`, camelCase keys may not bind. `FeatureSettingsTests` covers both.
- Require host-provided switches in `PbrRenderer` and `RenderPipeline`; an unconfigured host
  explicitly passes `new FeatureSwitches()`.
- Both the build switch and scene `Enabled` must allow a feature. Scene-disabled bloom may still
  declare a chain for graph culling; a disabled switch prevents the feature from running.
- Snapshot switches once per frame/schedule run. Mid-run changes apply next time. Do not subscribe
  the pipeline to `Changed`, which may run on another thread and race GPU state; hosts can call
  `BeginFrame` for an immediate transition before the next frame.
- Persistent render state implements `IRenderFeature.OnEnabledChanged`: retract shadows, SSAO,
  probe volumes and froxels on disable, including when already disabled at `Add`.
- Register engine features only in `PbrBuiltInFeatures`; games add features at spaced
  `PbrFeatureOrder` slots. Keep feature APIs on their features, without renderer forwarding or
  `…ForTest` accessors. The renderer exposes frame/output state and uploads.
- Upload buffers filled during graph recording in `BeforeSubmit`, after `Setup` and compilation.
- Serialize switch writes and `Changed` notifications in one critical section to preserve order;
  `Paradise.Features.CoyoteTest` covers this.

From the sibling [ParadiseSamples](https://github.com/ParadiseEngine/ParadiseSamples) repository,
list declarations with `dotnet run --project src/Paradise.Rendering.Sample -- --list-features`.
