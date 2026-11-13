#!/usr/bin/env bash
# Fails when more tests were skipped than the lane's budget. Remaining skips are intentional
# (Blender, Windows-only regressions, profiling builds, CJK fonts, quarantined known failures);
# raising a budget is a reviewed change, and a jump means coverage was lost.
set -euo pipefail

budget=${1:?usage: check-skip-budget.sh <budget>}
skipped=$(grep -E '^\s*skipped: [0-9]+' test-output.txt | grep -oE '[0-9]+' | awk '{ sum += $1 } END { print sum + 0 }')
echo "Skipped tests: $skipped (budget $budget)"
grep -E '^\s*skipped ' test-output.txt | sed 's/^\s*//' | sort | uniq -c || true
if [ "$skipped" -gt "$budget" ]; then
  echo "::error::$skipped tests skipped, above the budget of $budget"
  exit 1
fi
