#!/usr/bin/env bash
# Runs the test suites in two phases and appends every summary to test-output.txt.
#
#   run-tests.sh cpu   every test project except the GPU ones, in parallel
#   run-tests.sh gpu   the GPU projects, one at a time, with bounded test parallelism
#
# GPU_SKIP_PROJECTS names GPU projects (space-separated, by project name) a lane leaves to another.
#
# The phases are separate because GPU tests on a software adapter (lavapipe, WARP) saturate every
# core: run beside the CPU suites they starved timing-sensitive concurrency tests and, on Linux,
# exhausted the runner.
set -euo pipefail

mode=${1:?usage: run-tests.sh cpu|gpu}
gpu_projects=(
  src/Rendering/Paradise.Rendering.WebGPU.Test/Paradise.Rendering.WebGPU.Test.csproj
  src/Rendering/Paradise.Rendering.Pbr.Test/Paradise.Rendering.Pbr.Test.csproj
  src/UI/Paradise.Ui.ImGui.Test/Paradise.Ui.ImGui.Test.csproj
  src/UI/Paradise.Ui.Noesis.Test/Paradise.Ui.Noesis.Test.csproj
)

case "$mode" in
  cpu)
    cp ParadiseEngine.slnx ParadiseEngine.cpu.slnx
    dotnet sln ParadiseEngine.cpu.slnx remove "${gpu_projects[@]}"
    dotnet test --solution ParadiseEngine.cpu.slnx --no-build --output normal 2>&1 | tee -a test-output.txt
    ;;
  gpu)
    status=0
    for project in "${gpu_projects[@]}"; do
      name=$(basename "$project" .csproj)
      if [[ " ${GPU_SKIP_PROJECTS:-} " == *" $name "* ]]; then
        echo "Skipping $name on this lane (GPU_SKIP_PROJECTS)"
        continue
      fi
      dotnet test --project "$project" --no-build --output normal --maximum-parallel-tests "${GPU_TEST_PARALLELISM:-2}" 2>&1 \
        | tee -a test-output.txt || status=1
    done
    exit $status
    ;;
  *)
    echo "unknown mode: $mode" >&2
    exit 2
    ;;
esac
