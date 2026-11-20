# Concurrent code

Changes to locks, shared flags or queues require systematic tests in the matching Coyote suite;
stress tests alone do not cover interleavings. Suites are standalone runners, skipped by
`dotnet test`: ECS, ECS.Jobs, Rendering.WebGPU, Assets.Pipeline, Assets.Project, Cli, Ui.ImGui, Features and Hosting.
CI rewrites and explores each one (200 iterations) on pull requests that affect it and on every
push to main; a new runner needs a row in the `coyote` matrix of `.github/workflows/dotnet-test.yml`.

```bash
# Rewriting runs only in Release and requires the coyote CLI.
# Pass -p:ParadiseRuntimeAsync=false so every assembly in the graph keeps compiler-generated
# async state machines (Coyote 1.7.11 does not rewrite .NET 11 runtime-async IL).
dotnet build src/Rendering/Paradise.Rendering.WebGPU.CoyoteTest -c Release -p:ParadiseRuntimeAsync=false
dotnet run --project src/Rendering/Paradise.Rendering.WebGPU.CoyoteTest -c Release -- 200

# Persistent job-pool wave lifetime and disposal interleavings.
dotnet build src/Simulation/Paradise.ECS.Jobs.CoyoteTest -c Release -p:ParadiseRuntimeAsync=false
dotnet run --project src/Simulation/Paradise.ECS.Jobs.CoyoteTest -c Release --no-build -- 200
```

- Lock on `object`: Coyote 1.7.11 rewrites `Monitor`, not `System.Threading.Lock.EnterScope`.
- Keep runtime async off for Coyote builds. `src/Directory.Build.props` turns it on
  (`Features=runtime-async=on`) only for `*.Test` projects and never for libraries or
  `*.CoyoteTest`; pass `-p:ParadiseRuntimeAsync=false` on the CLI so a test project that a Coyote
  build happens to reference stays on compiler state machines too. `src/Directory.Build.targets` centralizes the
  `CoyoteRewrite` target and sets `DOTNET_ROLL_FORWARD=Major` so `Microsoft.Coyote.CLI 1.7.11`
  (`net8.0`) runs on `.NET 11` hosts without a `.NET 8` runtime.
- Separate managed coordination from native calls; `CaptureQueue` is the renderer example.
- Await joins; `Task.WaitAll` can appear as a deadlock. Keep hang detection enabled.
- For interleaving regressions, a temporary defect reintroduction can confirm that the systematic
  test exercises the failure; restore the fix afterward.
- Zio `CopyFileCross` resolves to the physical filesystem and bypasses wrapper `OpenFileImpl`.
  Memory filesystems can permit operations the OS refuses; exercise the physical-filesystem
  behavior when it affects correctness.
