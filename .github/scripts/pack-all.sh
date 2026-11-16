#!/usr/bin/env bash
# Packs every packable project at one version into one directory, and checks what a release
# relies on: every package has a README row, every packable project produced a package, and the
# source generators ship inside their runtime packages.
#
# Usage: pack-all.sh <version> <output-directory>   (CONFIGURATION defaults to Release)
set -euo pipefail

version="$1"
output="$2"
configuration="${CONFIGURATION:-Release}"

# Every project MSBuild reports as packable, so a new library cannot be forgotten. Tests,
# benchmarks, samples and generators opt out with IsPackable=false. Source generators ship
# inside Paradise.BT, Paradise.ECS and Paradise.Authoring (analyzers/dotnet/cs); Paradise.Cli
# packs as a dotnet tool (PackAsTool).
projects=()
ids=()
while IFS= read -r project; do
  properties=$(dotnet msbuild "$project" -getProperty:IsPackable -getProperty:PackageId)
  if [[ $(jq -r .Properties.IsPackable <<< "$properties") == "true" ]]; then
    projects+=("$project")
    ids+=("$(jq -r .Properties.PackageId <<< "$properties")")
  fi
done < <(git ls-files 'src/*.csproj' | sort)
echo "Packing ${#projects[@]} projects: ${ids[*]}"

# A package nobody can find is a package nobody uses: each one needs a README row.
missing=0
for id in "${ids[@]}"; do
  if ! grep -qF "[$id](" README.md; then
    echo "::error::$id is packable but has no row in README.md"
    missing=1
  fi
done
[[ $missing -eq 0 ]] || exit 1

for project in "${projects[@]}"; do
  dotnet pack "$project" \
    -c "$configuration" \
    -o "$output" \
    -p:ContinuousIntegrationBuild=true \
    -p:Version="$version"
done

count=$(ls "$output"/*.nupkg | wc -l)
if [[ "$count" -ne "${#projects[@]}" ]]; then
  echo "::error::Expected ${#projects[@]} packages, found $count"
  exit 1
fi

# The generators must travel inside their runtime packages, or consumers silently get no codegen.
for pair in Paradise.ECS:Paradise.ECS.Generators Paradise.BT:Paradise.BT.Generators Paradise.Authoring:Paradise.Authoring.Generators; do
  package="$output/${pair%%:*}.$version.nupkg"
  if ! unzip -l "$package" | grep -q "analyzers/dotnet/cs/${pair##*:}.dll"; then
    echo "::error::${pair%%:*} does not ship ${pair##*:}"
    exit 1
  fi
done
