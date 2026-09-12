# C# tray extension SDK

`Paradise.Cli.Extensibility` is the small, dependency-free contract assembly shared by the
managed CLI/tray host and extension DLLs. It does not contain a compiler, game runtime,
native menu code, or a JSON menu schema.

A public concrete `ITrayExtension` with a public parameterless constructor contributes one
or more `TrayTaskGroup` values from `CreateTaskGroups(ITrayExtensionContext)`. The host loads
it through the existing `[extensions] assemblies` list in `assets/project.toml`:

```toml
[extensions]
assemblies = [".editor/extensions/dialogue/MyDialogueExtension.dll"]
```

When the extension's source belongs to the project, list its project path:

```toml
[extensions]
projects = ["tools/DialogueExtension/DialogueExtension.csproj"]
```

The loader incrementally publishes the C# project in Release on each watcher start and loads
`.editor/extensions/DialogueExtension.dll` with its dependencies. The DLL uses the project's
basename; do not override `AssemblyName` to a different name. A failed publish stops startup even
if an old DLL exists. Restart the watcher to pick up extension source edits; already-loaded
assemblies are not replaced during a session.

All menu labels, task callbacks, watched inputs, output exclusions and initial watch state
are C#. The manifest only locates the extension project or a prebuilt DLL. No `authoring/tray-tasks.json` is read.

`Outputs` may name files or directories. A declared output excludes its exact path and all
separator-delimited descendants, including create/delete/rename events when it does not exist.
For example, `authoring/dialogue/generated` excludes `generated/nested/result.story`, but not
`authoring/dialogue/generated-other/result.story`. Input and output matching use the same
platform case rules; no filesystem existence check is needed.

```csharp
using Paradise.Cli;

public sealed class DialogueExtension : ITrayExtension
{
    public IReadOnlyList<TrayTaskGroup> CreateTaskGroups(ITrayExtensionContext context) =>
    [
        new()
        {
            Id = "dialogue",
            Label = "Dialogue",
            AutoTask = "compile",
            Inputs = [new("authoring/dialogue", ["*.story"])],
            Outputs = ["story/compiled.json"],
            Tasks =
            [
                new("compile", "Compile Now", token => context.RunProcessAsync(
                    "dotnet", ["build", "Game.Launcher", "-t:CompileStory"], token)),
            ],
            OpenDirectory = "authoring/dialogue",
        },
    ];
}
```

Reference the matching published SDK, set `EnableDynamicLoading=true` on the extension
project, and exclude the SDK's runtime assets (`ExcludeAssets="runtime"`). Deploy the
extension DLL, `.deps.json` and its private dependencies together. The loader explicitly
shares the host's contract assembly rather than loading a plugin-private copy. The SDK ABI
uses assembly version `1.0.0.0`; package versions follow engine releases.

Registration is synchronous, validated, snapshotted, and atomic per extension. It must not
start background work. Duplicate group/task IDs and malformed paths/callbacks are refused.
A broken extension is diagnosed without preventing other extensions or built-in actions.
Tray-only classes are constructed only for watch sessions, not build/verify/play commands.

Tasks are ordinary `Func<CancellationToken, Task<int>>` callbacks and can call C# directly;
using a subprocess is optional. The host schedules them off the native menu thread. It owns
watch debounce, serialization, Cancel and status. `RunProcessAsync` preserves argument
boundaries and stops the child process tree on cancellation. An in-process callback must
cooperate with cancellation: managed code cannot be forcibly aborted safely.

Extensions may implement `IDisposable`. The host disposes dynamically constructed instances
once, after the command ends; a watch first cancels/joins tasks and tears down native menus.
A class implementing both `IAssetImporter` and `ITrayExtension` is constructed/disposed once.

DLLs are loaded when the watcher starts. Replace/rebuild the DLL and restart the watcher to
load new C# code. This is not collectible assembly hot reload. Story input watching remains
live during the session. Only trusted local DLLs should be configured: a load context is not
a sandbox. The CLI/tray host is managed and untrimmed; the shipped game remains independent.
