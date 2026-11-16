#!/usr/bin/env bash
# Writes $SOLUTION: ParadiseEngine.slnx without the projects UNAFFECTED lists (space-separated, as
# select-tests.py reports them). Projects left in it still build what they reference.
set -euo pipefail

solution=${SOLUTION:?set SOLUTION to the solution file to write}
cp ParadiseEngine.slnx "$solution"
read -ra unaffected <<<"${UNAFFECTED:-}"
if ((${#unaffected[@]})); then
  dotnet sln "$solution" remove "${unaffected[@]}" >/dev/null
fi
echo "$solution builds:"
grep -oE 'Path="[^"]+\.csproj"' "$solution" | sed -E 's/^Path="(.*)"$/  \1/' || echo "  (nothing)"
