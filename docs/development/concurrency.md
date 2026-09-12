# Concurrent code

Changes to locks, shared flags or queues require systematic tests in the matching Coyote suite;
stress tests alone do not cover interleavings. Suites are standalone runners, skipped by
`dotnet test`: ECS, Rendering.WebGPU, Assets.Pipeline, Assets.Project, Cli, Ui.ImGui, Features and Hosting.

```bash
# Rewriting runs only in Release and requires the coyote CLI.
dotnet build src/Paradise.Rendering.WebGPU.CoyoteTest -c Release
dotnet run --project src/Paradise.Rendering.WebGPU.CoyoteTest -c Release -- 200
```

- Lock on `object`: Coyote 1.7.11 rewrites `Monitor`, not `System.Threading.Lock.EnterScope`.
- Separate managed coordination from native calls; `CaptureQueue` is the renderer example.
- Await joins; `Task.WaitAll` can appear as a deadlock. Keep hang detection enabled.
- For interleaving regressions, a temporary defect reintroduction can confirm that the systematic
  test exercises the failure; restore the fix afterward.
- Zio `CopyFileCross` resolves to the physical filesystem and bypasses wrapper `OpenFileImpl`.
  Memory filesystems can permit operations the OS refuses; exercise the physical-filesystem
  behavior when it affects correctness.
