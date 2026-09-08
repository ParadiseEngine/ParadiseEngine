# Game hosting

`Paradise.Hosting` owns the application loops. `Paradise.Hosting.Desktop` supplies the SDL or
offscreen platform and reads feature configuration. Neither package requires PBR or a UI toolkit.
The first consumer is ShiningPie: its application implements factories for simulation and
presentation while the engine owns timing, threads, resize delivery, capture scheduling and shutdown.

```csharp
var options = HostCommandLine.Parse(args, new WindowOptions("My Game", 1280, 720));
var switches = HostConfiguration.Load(content, engineConfigPath,
    Environment.GetEnvironmentVariable("PARADISE_FEATURES"),
    HostCommandLine.Value(args, "--features"));
return DesktopHost.Run(application, options, switches, logger);
```

The caller supplies a Zio content mount, a configuration path and a non-null `ILogger`. A missing
default configuration file is allowed; callers should reject a missing explicitly requested file.
File, environment and command-line overrides apply in that order. Declare game features through
the supplied switchboard and pass that same instance into the ECS schedule and render pipeline.
Game-specific scene documents, actions, UI policies and animation choices remain in the game.

## Application callbacks

- `IHostApplication.CreateSimulation` runs on the simulation thread. It creates the world owner,
  simulation and any simulation-thread UI. Its completion precedes presentation creation.
- `IHostSimulation.HandleInput` receives timestamped input before the first owed tick. `Tick`
  advances one fixed step. `AfterTicks` runs once after the catch-up batch, suitable for refreshing
  UI from the newest published state. The host caps catch-up at 250 ms by default.
- `IHostApplication.CreatePresentation` runs on the rendering thread with a surface created on
  the main thread. Its result implements `Render`, `Resize` and `CaptureAsync`.
- `Render(elapsed, delta)` produces one frame. `CaptureAsync(path)` queues a capture of the next
  `Render` and returns a task covering both readback and output. The host queues it immediately
  before the requested frame and waits after submission, keeping at most one capture in flight.
  `ShutdownTimeout` bounds each capture wait as well as final joins, so an unavailable target
  cannot stall an otherwise unbounded run indefinitely.

Factories must clean up partially created resources if they throw. Returned components are
disposed by the host on their creation threads. The host stops presentation and waits for pending
captures before disposing simulation, then disposes the window and platform on the main thread.
Any worker failure stops the run and returns a nonzero exit code. A worker that exceeds the shutdown
budget keeps resources it may still access alive; a late completion can finish worker cleanup.
Native UI libraries with process-scoped lifetimes retain their own documented teardown rules.

## Snapshot ownership

`SnapshotStream<T>` transports `WorldSnapshot<T>` without depending on a game's generated ECS types.
The producer creates worlds and copies completed simulation state into a recycled or fresh world.
It publishes that world with a monotonically increasing tick number. Consumers borrow snapshots
through `ISnapshotStream<T>.TryRead` and return each world exactly once through `Recycle` after
their final read. A game-specific sampler can retain two snapshots to interpolate component values.

`TryTakeRecycled` returns only worlds safe to overwrite. The newest published world remains reserved
as the producer's next read world until superseded. The bounded queue trims only unread snapshots;
borrowed worlds are retained until returned. The stream does not dispose worlds, and consumers must
finish before the world owner is disposed. Warmed reuse allocates no per-snapshot objects.

## Common launch options

`--headless` selects an offscreen surface; it defaults to 120 frames. `--frames N` bounds rendered
frames. `--hold W,A` supplies initial held keys in headless mode. `--screenshot path.png` captures
the last frame of a bounded run, or frame 120 of an unbounded run. `--capture-frame 30,60,90` selects
frames; `--capture-every 30` continues at an interval after any explicit list. Multiple captures add
a five-digit frame suffix to the filename. Invalid or impossible capture options fail early.

## Validation

```sh
dotnet test --project src/Paradise.Hosting.Test/Paradise.Hosting.Test.csproj --output normal
dotnet test --project src/Paradise.Hosting.Desktop.Test/Paradise.Hosting.Desktop.Test.csproj --output normal
dotnet build src/Paradise.Hosting.CoyoteTest -c Release
dotnet run --project src/Paradise.Hosting.CoyoteTest -c Release --no-build -- 200
```

The Coyote runner tests managed shutdown, failure and snapshot ownership independently of native
threads and GPU calls. Native-thread tests check that the orchestration follows those lifetimes.

For package validation, pack the engine libraries at one preview version, including Features,
Features.Toml, Hosting and Hosting.Desktop, into a local feed. Restore the game with that feed plus
its usual package sources. Keep `ParadiseUseEngineSource=false` to exercise package metadata,
generators and build targets. The preview must be published before an ordinary CI restore can use it.
