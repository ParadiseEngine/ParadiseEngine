#!/usr/bin/env bash
# Steady-state frames of the reference composition allocate nothing. Runs the allocation benchmark
# (src/Runtime/Paradise.Runtime.Benchmarks) at a zero budget: a regression fails here, and the report
# above the failure names the phase or render feature that allocated. WITH_GPU=1 adds the WebGPU backend,
# with fewer, smaller frames for a software adapter.
set -euo pipefail

configuration=${CONFIGURATION:-Debug}
benchmark=(dotnet run --project src/Runtime/Paradise.Runtime.Benchmarks -c "$configuration" --no-build --)

"${benchmark[@]}" --budget 0
"${benchmark[@]}" --budget 0 --workload showcase --entities 64
"${benchmark[@]}" --budget 0 --entities 256 --churn 16
if [[ "${WITH_GPU:-}" == 1 ]]; then
  gpu=(--budget 0 --backend webgpu --width 320 --height 180 --warmup 30 --frames 120)
  "${benchmark[@]}" "${gpu[@]}"
  "${benchmark[@]}" "${gpu[@]}" --workload showcase --entities 64 --churn 8
fi
