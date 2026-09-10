# Project task submenus

`paradise assets watch` can add project-defined parent menus to its existing Windows/macOS
tray. It does not create another icon or application. The generic host does not reference
any project's compiler or runtime vocabulary.

The watch command reads `authoring/tray-tasks.json` relative to the project root once at
startup. No file means no additional menus; invalid configuration is reported without
preventing normal asset watching. Restart the watcher after editing the configuration.

```json
{
  "version": 1,
  "groups": [{
    "id": "dialogue",
    "label": "Dialogue",
    "autoWatch": false,
    "autoWatchLabel": "Auto-watch Scripts",
    "autoTask": "compile",
    "debounceMilliseconds": 300,
    "inputs": [{ "path": "authoring/dialogue", "patterns": ["*.story", "*.project"], "recursive": true }],
    "outputs": ["story/compiled.json"],
    "tasks": [
      { "id": "compile", "label": "Compile Scripts Now", "executable": "dotnet", "arguments": ["build", "Game.Launcher", "-t:GenerateDialogue"] },
      { "id": "check", "label": "Check Artifacts", "executable": "dotnet", "arguments": ["build", "Game.Launcher", "-t:CheckDialogue"] }
    ],
    "openDirectory": "authoring/dialogue",
    "openDirectoryLabel": "Open Dialogue Folder"
  }]
}
```

Each group has an auto-watch checkbox, its configured actions, Cancel Running Task, a
read-only last-result line, and an optional source-folder action. State is refreshed when
the menu opens. Native callbacks only signal requests; the worker performs tasks serially.
Diagnostics inherit the watcher's console/log. No shell command-line concatenation is used:
executables receive the configured argument array, with the project root as working directory.
`dotnet` is resolved through the existing locator for GUI hosts with a restricted PATH.

Task configuration is trusted project build configuration, not a sandbox. Starting a watcher
with `autoWatch: true` authorizes those configured tasks to run, including in console-only
mode. Review task executables/arguments just as you would review project build targets.

Auto-watch defaults to false. Its checkbox is session-local and independent of asset build,
play mode, and external build/watch processes. Enabling it schedules a catch-up pass. File
changes debounce from the latest event; an edit during a running task remains pending for
a subsequent pass. Disabling auto-watch discards only pending automatic work. Explicit
Compile/Check actions remain available, and an active task continues unless cancelled.
Manual task actions are disabled while that group is running. Cancel ends its process tree
and clears pending requests; a later new edit may start a new automatic pass. Stopping the
watcher cancels its children and joins the task worker before disposal.

Inputs are project-relative paths with `/` separators and no traversal. Omit `patterns` for
an exact file; otherwise patterns match filenames under that input directory, recursively by
default. Structural events include both sides of renames and deleted directories. Inputs may
live outside `assets/`. The host observes declared roots; language-specific project exclusions
and source resolution remain the compiler's responsibility. Generated outputs, `.git`,
`.editor`, `bin`, and `obj` never request work. Compiler publication must still coordinate with
other processes writing its artifacts; serial execution in this watcher is not a global lock.

Filesystem-watch startup failures disable only automatic watching. Manual task execution stays
available. Buffer overflow requests a fresh pass; other watcher failures disable its checkbox.
The task configuration limits group/task counts and rejects unknown fields, invalid versions,
duplicate IDs, missing automatic tasks, and invalid paths.

Windows uses native popup submenus and checked/disabled entries; macOS uses `NSMenu` submenus
and a shared managed entry model. Linux, CI, and `--no-tray` retain the existing console-only
fallback. The managed model and state machine have unit tests and Coyote coverage; native menu
appearance and interaction must additionally be verified on a Windows/macOS desktop.
