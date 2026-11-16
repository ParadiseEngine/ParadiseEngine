#!/usr/bin/env bash
# Runs the test suites in two phases and appends every summary to test-output.txt.
#
#   run-tests.sh cpu   every test project in the solution except the GPU ones, in parallel
#   run-tests.sh gpu   the GPU projects in the solution, one at a time, with bounded test parallelism
#
# SOLUTION names the solution to test (ParadiseEngine.slnx by default); pull requests pass one
# narrowed to the projects they affect (see select-tests.py). GPU_SKIP_PROJECTS names GPU projects
# (space-separated, by project name) a lane leaves to another.
#
# The phases are separate because GPU tests on a software adapter (lavapipe, WARP) saturate every
# core: run beside the CPU suites they starved timing-sensitive concurrency tests and, on Linux,
# exhausted the runner.
set -euo pipefail

mode=${1:?usage: run-tests.sh cpu|gpu}
solution=${SOLUTION:-ParadiseEngine.slnx}
gpu_projects=(
  src/Rendering/Paradise.Rendering.WebGPU.Test/Paradise.Rendering.WebGPU.Test.csproj
  src/Rendering/Paradise.Rendering.Pbr.Test/Paradise.Rendering.Pbr.Test.csproj
  src/Rendering/Paradise.Rendering.Sdf.Test/Paradise.Rendering.Sdf.Test.csproj
  src/UI/Paradise.Ui.ImGui.Test/Paradise.Ui.ImGui.Test.csproj
  src/UI/Paradise.Ui.Noesis.Test/Paradise.Ui.Noesis.Test.csproj
)

projects_in() {
  grep -oE 'Path="[^"]+\.csproj"' "$1" | sed -E 's/^Path="(.*)"$/\1/' | tr '\\' '/' || true
}

in_solution() {
  projects_in "$solution" | grep -qxF "$1"
}

case "$mode" in
  cpu)
    cp "$solution" ParadiseEngine.cpu.slnx
    present=()
    for project in "${gpu_projects[@]}"; do
      if in_solution "$project"; then present+=("$project"); fi
    done
    if ((${#present[@]})); then dotnet sln ParadiseEngine.cpu.slnx remove "${present[@]}"; fi
    tests=0
    while IFS= read -r project; do
      if grep -q '<IsTestProject>true</IsTestProject>' "$project"; then tests=$((tests + 1)); fi
    done < <(projects_in ParadiseEngine.cpu.slnx)
    if ((tests == 0)); then
      echo "No CPU test projects in $solution"
      exit 0
    fi
    dotnet test --solution ParadiseEngine.cpu.slnx --no-build --output normal 2>&1 | tee -a test-output.txt
    ;;
  gpu)
    status=0
    for project in "${gpu_projects[@]}"; do
      name=$(basename "$project" .csproj)
      if ! in_solution "$project"; then
        echo "Skipping $name: not in $solution"
        continue
      fi
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
