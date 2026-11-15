#!/usr/bin/env bash
# Fails when more tests were skipped than the lane's budget. Remaining skips are intentional
# (Blender, Windows-only regressions, profiling builds, CJK fonts, quarantined known failures);
# raising a budget is a reviewed change, and a jump means coverage was lost.
set -euo pipefail

budget=${1:?usage: check-skip-budget.sh <budget>}
if [ ! -s test-output.txt ]; then
  echo "::error::test-output.txt is missing or empty; the test steps did not record their summaries"
  exit 1
fi

# TUnit colours its summaries even without a terminal ("ESC[m  skipped: 5"); strip the escapes so
# the anchored patterns see the text.
plain=$(sed -E 's/\x1b\[[0-9;]*m//g' test-output.txt)
runs=$(grep -cE '^\s*skipped: [0-9]+' <<<"$plain" || true)
if [ "$runs" -eq 0 ]; then
  echo "::error::no test-run summaries found in test-output.txt"
  exit 1
fi

skipped=$( (grep -E '^\s*skipped: [0-9]+' <<<"$plain" || true) | grep -oE '[0-9]+' | awk '{ sum += $1 } END { print sum + 0 }')
echo "Skipped tests: $skipped across $runs test run(s) (budget $budget)"
(grep -E '^\s*skipped ' <<<"$plain" || true) | sed -E 's/^\s*//' | sort | uniq -c
if [ "$skipped" -gt "$budget" ]; then
  echo "::error::$skipped tests skipped, above the budget of $budget"
  exit 1
fi
