# Existing watch tray: C# DLL extensions

`paradise assets watch` retains its existing Windows/AppKit tray and built-in asset/play
controls. It now discovers `ITrayExtension` alongside `IAssetImporter` from the existing
`[extensions] assemblies` list in `assets/project.toml`. There is no new tray application,
and no JSON menu configuration file.

Source extensions can use `[extensions] projects = ["tools/MyTray/MyTray.csproj"]`.
Their DLLs use the project basename and are published into `.editor/extensions/`.
The loader publishes these projects before discovery on each watcher start, including when an old
DLL already exists. Compiler diagnostics go to the watcher's output; a failed build stops startup
without falling back to stale code. DLL-only `assemblies` entries retain their existing behavior.
Extensions remain loaded for the session: restart the watcher to rebuild and load source changes.

The public contract lives in `Paradise.Cli.Extensibility`; see
[`Paradise.Cli.Extensibility`](../../Paradise.Cli.Extensibility/README.md) for a complete
C# example and packaging guidance. An extension returns parent task groups containing labels,
callbacks, inputs and output exclusions. The host validates and snapshots each contribution
before native menu construction. One malformed/duplicate contribution is skipped atomically,
with diagnostics, while valid extensions and built-in actions remain available.

Each parent submenu contains Auto-watch (off by default), its C# actions, Cancel, last-result
status, and an optional Open Folder action. Auto-watch is session-local and independent of
asset building or play mode. Enabling it schedules a catch-up run. Source edits are debounced;
an edit during a compile survives as one follow-up. Disabling auto-watch drops queued
automatic work, not active or explicitly requested work. The final compiled source set is
the backend's responsibility; an extension must declare all authoring roots it wants watched.

Callbacks run serially on the task worker, never in native menu callbacks. Source additions,
removals, both sides of renames and directory changes are observed. Outputs may be files or
directories: their exact paths and descendants are excluded, even before creation or after
deletion. Separator-aware matching keeps similarly named source siblings observable. Outputs
and `.editor`, `.git`, `bin`, `obj` do not trigger their own compilation. A fatal watcher error disables
Auto-watch but keeps manual tasks usable. Native menus refresh status when opened.

`ITrayExtensionContext.RunProcessAsync` uses the existing argument-array process runner and
project working directory. Cancellation stops a child process tree. C# callbacks that do not
use that runner still need to honor their cancellation token; the host joins outstanding work
before disposing extensions. Exceptions become task failures rather than escaping into a
native callback. Detailed diagnostics use the existing watcher console/log.

DLL discovery is startup-based, not automatic unloading/reloading of changed assemblies.
Restart the watcher after rebuilding a DLL or changing the manifest. Noncollectible assemblies
can remain file-mapped on Windows until process exit. Constructors and registrations should
be lightweight; only trusted extensions are allowed because load contexts are not sandboxes.

Only watch commands instantiate tray-only extensions. A dual importer/tray type is loaded
once per command, and all owned disposable instances are released once in reverse order.
Built-in/caller-supplied importer instances are not owned by the DLL loader. Dependency or
constructor failures are named and do not prevent valid assemblies from loading.

The CLI is deliberately managed/untrimmed. This has no bearing on a game's NativeAOT build;
the game should not reference this host or tooling SDK. Windows/macOS use native menus;
Linux/headless/`--no-tray` keep console behavior (explicitly enabled automatic tasks still run).

## Verification

The CLI tests load a separately built fixture DLL, resolve a private dependency, prove the
host contract wins over a duplicate beside the plugin, invoke a C# callback, preserve importer
behavior, exercise failing constructors/registration, and check one-time disposal. Watch/menu
and Coyote state tests cover cancellation, single execution and edits during compilation.
Native Windows/macOS interaction still needs a smoke test on those platforms.
