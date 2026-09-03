# Logging

Engine libraries report diagnostics through `Microsoft.Extensions.Logging.Abstractions`
(`ILogger`), and reference **no provider**. Which sink a game logs to is the host's decision.

This is issue #232's seam. What follows is the decision and the reasons, so the next person does
not have to re-derive them.

## The rule

- **An engine library takes an `ILogger`** where it used to take an `Action<string>`, and defaults
  to `NullLogger.Instance` where it used to default to a no-op. It never names `Console`.
- **An engine package references `Microsoft.Extensions.Logging.Abstractions` only.** Adding a
  provider (ZLogger, Serilog, `Microsoft.Extensions.Logging.Console`) to a `Paradise.*` library is
  the mistake this rule exists to prevent: it decides for every host at once.
- **A host installs the sink.** `Paradise.Diagnostics` is one, for hosts with no other opinion;
  `Paradise.Cli.Host` uses it. A game with a logging stack ignores that package and puts its own
  provider behind the same `ILogger`.
- **Log through `[LoggerMessage]`, not `logger.LogInformation(...)`,** at anything that could be
  hot. See below.

## Why Abstractions and not a hand-written interface

A 30-line `IParadiseLog` was the front-runner while this repo still had `netstandard2.1` targets,
because the package's dependency footprint differs sharply by target framework:

| TFM group | Transitive dependencies |
| --- | --- |
| `netstandard2.0` | DI.Abstractions, DiagnosticSource, System.Buffers, System.Memory |
| `net8.0` / `net9.0` | DI.Abstractions, DiagnosticSource |
| **`net10.0`** | **DI.Abstractions — whose own `net10.0` group is empty** |

`System.Diagnostics.DiagnosticSource` is in-box from net10, and every project here targets
`net10.0` (the three `netstandard2.0` ones are source generators, which do not log). So the real
cost is two assemblies and no tree at all.

**If anything here is ever retargeted below `net10.0`, re-run that comparison** before assuming
this is still nearly free.

`Microsoft.Extensions.DependencyInjection.Abstractions` is `IServiceProvider` extension types. It
contains no container, and nothing about this obliges a host to use one.

What the hand-written alternative would have had to grow: a level enum, a filter, a category
notion, a thread-safe default sink, an interpolated-string handler per level to get the
zero-allocation property, and a null object. `[LoggerMessage]` is all of that, generated, and
every host that adopts the engine skips writing an adapter.

## `[LoggerMessage]`, and why not `ILogger.Log*`

The generator ships **inside** the Abstractions package
(`analyzers/dotnet/roslyn*/cs/Microsoft.Extensions.Logging.Generators.dll`) — there is no second
reference to add. It emits a static method that tests `IsEnabled` **before** touching any
argument and writes through a cached delegate: no boxing, no `object[]`, no runtime template
parse, and it is compile-time, so it survives trimming and NativeAOT with no IL warnings.

Two constraints it imposes, both discovered by the build refusing:

- **The `ILogger` parameter cannot be nullable.** The generated body calls `logger.IsEnabled(...)`
  unguarded, so `ILogger?` fails with CS8602 *inside generated code*. Carry
  `NullLogger.Instance` instead — which is why `ImportContext.Log` is non-nullable.
- **It generates into a `partial` class.** Where log sites are spread across several classes in
  one file, give them one `internal static partial class` to live in (`ImporterLog` does this).

## Rendering an engine value: the `UPath` case

This is what motivated the issue, and the part most likely to be re-broken.

A Zio `UPath` is `/`-separated and rooted at its mount, so a physical filesystem renders
`C:\proj\x` as `/mnt/c/proj/x` — correct inside the abstraction and useless to a person. The layer
that produces the message must not translate it: `ConvertPathToInternal` throws
`NotSupportedException` on a `MemoryFileSystem`, so "just translate it there" means a try/catch in
every reader, in types whose whole point is not caring which filesystem they were handed.

**So a library logs the `UPath` itself, as a template argument, never a pre-rendered string.**

```csharp
[LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "minted: {Sidecar}")]
private static partial void LogMinted(ILogger logger, UPath sidecar);
```

The host renders it. `ParadiseConsoleOptions.RenderValue` is that hook, and it works because a log
record reaches a provider as `IReadOnlyList<KeyValuePair<string, object?>>` with the argument still
a boxed `UPath` — not a string. `Paradise.Cli.Host`'s `PipelineLog` installs one that prints a path
under `assets/` project-relative and anything else as a host path.

`SidecarMaintainer` used to hold the first half of that rule itself, in a `Display` helper that
trimmed the assets root off the front. It was deleted: it is one host's preference baked into the
layer that cannot know it, and it never had the information for the second half.

Costs, stated honestly: one box per logged `UPath` (only on an enabled level, and only at sites
that are not hot), and the renderer runs per argument of every enabled message. When no renderer
is installed, or when the installed one declines every argument in a message, the sink uses the
formatter the logging call supplied and re-renders nothing — so the common case stays exactly
correct rather than depending on this reimplementation of MEL's formatting.

## Foreign threads

The engine logs from threads it did not create: Dawn's uncaptured-error callback, Noesis's log
callback, SDL. `ILogger` promises no thread affinity, so **thread safety is a requirement on the
sink**, not something the abstraction provides. A host installing its own provider owns the same
requirement.

The two sinks here meet it differently, and the difference is the useful part:

- **`ParadiseConsoleLoggerProvider` holds a lock**, because what it shares is a `TextWriter`. Two
  threads must not interleave halfway through a line — that is how a device-lost report becomes
  unreadable — and the prefix, the message and an exception are several writes that have to land
  together. No concurrent collection helps with that; the resource is the writer.
- **`CollectingLogger` holds none.** Its records live in a `ConcurrentQueue`, and every operation
  on it is single-step — append one record, snapshot, clear — so there is nothing for a lock to
  make atomic. Appending is then lock-free, which is what a native callback thread wants.

The lock that remains is on an `object` rather than a `System.Threading.Lock`, because it is
reachable from the Coyote suites — see AGENTS.md. Coyote schedules concurrent collections too, so
the queue is equally visible to it.

## Testing what the engine reported

`CollectingLogger` (in `Paradise.Diagnostics`) keeps what it was told, so behaviours that were
previously observable only as console output can be asserted on. It ships in the product package
rather than a test helper because a game asserting that its importer chain reported a problem has
no other way in.

## Where the logger enters, per subsystem

Every one of these takes an optional `ILogger` and defaults to silence:

| Type | Parameter | Reaches |
| --- | --- | --- |
| `WebGpuRenderer` | `logger:` (also on `CreateHeadless`) | `WebGpuDevice`'s Dawn callbacks, `DeferredDestructionQueue` |
| `SdlWindowPlatform` | `logger:` | every `SdlWindow` it creates |
| `NoesisViewCore` | `logger:` | Noesis's own log callback, and the view-loaded line |
| `PbrRenderer` | `logger:` | joint-palette overflow, cluster stats |
| `SidecarMaintainer` / `AssetWatcher` / `BuildRunner` / `AssetMover` / `ArtifactCache` | `logger:` | the asset pipeline |

Three notes that are not obvious from the signatures:

- **Dawn's callbacks are no longer `static` lambdas.** They capture the logger, which costs one
  closure per device — once, at creation — and is what makes them routable at all.
- **Noesis's log callback is process-global.** `NoesisViewCore` captures its own logger into it, so
  with two cores in one process the first created owns Noesis's log for the whole run. There is no
  per-core seam in Noesis to do better; the surrounding code already says the same about providers.
- **`PARADISE_CLUSTER_DEBUG=1` is gone.** The cluster froxel dump is `LogLevel.Debug` now, which is
  what the issue asked for — it stops being the same volume as a device-lost report — and it also
  removes a `GetEnvironmentVariable` call from every frame. Enable `Debug` on that logger instead.

Where an `IsEnabled` check appears explicitly in front of a `[LoggerMessage]` call, it is because
something *before* the formatting costs real work: decoding Dawn's UTF-8 message, or walking every
froxel word to count set bits. `[LoggerMessage]` guards the formatting, not the arguments you
compute to pass it.

## What deliberately still writes to `Console`

Program output is not a diagnostic. `Paradise.Authoring.SchemaDump` writes its dump to stdout
because that **is** its output. `Paradise.Cli.Host`'s `Verbs` prints verb results
(`verify: 3 error(s)`) the same way and always should — only the diagnostics it passes *into* the
pipeline go through the logger. And a host has to pick a sink to see anything: the samples do it
in one line (`Paradise.Rendering.Sample`'s `EngineLog`), which is what keeps them printing what
they always printed instead of going quiet.

`EventSource` / `ActivitySource` are the right home for frame timings and GPU submit counts —
in-box, near-zero cost with no listener, readable by `dotnet-trace`. They are complementary to
this and not a substitute: neither produces a human-readable log.
