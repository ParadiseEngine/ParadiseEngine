#!/usr/bin/env bash
# Rebuilds third_party/ktx/browser-wasm/ktx.a: libktx (KTX-Software 4.4.2, the release the
# vendored osx-arm64 dylib and Ktx2.NET's struct layouts match) as a static wasm32 archive
# for the Mono browser-wasm relink.
#
# The archive must be compiled by the emscripten that links dotnet.native.wasm, so this uses
# the wasm-tools workload's own emscripten pack rather than a separate emsdk. Objects carry
# -fwasm-exceptions because the relink does (mixing exception models fails to link), and no
# -pthread because browser-wasm is single-threaded (atomics would demand shared memory).
#
# Named ktx.a, not libktx.a: the browser-wasm pinvoke table is keyed by the native file's name,
# which must equal the [DllImport("ktx")] module name Ktx2.NET uses.
#
# Usage: third_party/ktx/build-browser-wasm.sh  (needs git, cmake, ninja, the wasm-tools workload)
set -euo pipefail

KTX_TAG=v4.4.2
EMSCRIPTEN_PACK_PREFIX=Microsoft.NET.Runtime.Emscripten.3.1.56

repo_root=$(cd "$(dirname "$0")/../.." && pwd)
output="$repo_root/third_party/ktx/browser-wasm/ktx.a"

dotnet_root=${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}
host_rid=$(uname -s | tr '[:upper:]' '[:lower:]' | sed 's/darwin/osx/')-$(uname -m | sed 's/x86_64/x64/;s/aarch64/arm64/')
pack() {
    local dir
    dir=$(ls -d "$dotnet_root/packs/$EMSCRIPTEN_PACK_PREFIX.$1.$host_rid"/*/ 2>/dev/null | sort -V | tail -1)
    [[ -n "$dir" ]] || { echo "error: $EMSCRIPTEN_PACK_PREFIX.$1.$host_rid not found; run 'dotnet workload install wasm-tools'" >&2; exit 1; }
    echo "${dir%/}/tools"
}
sdk=$(pack Sdk)
export DOTNET_EMSCRIPTEN_LLVM_ROOT="$sdk/bin"
export DOTNET_EMSCRIPTEN_BINARYEN_ROOT="$sdk"
export DOTNET_EMSCRIPTEN_NODE_JS="$(pack Node)/bin/node"
export EM_CACHE="$(pack Cache)/emscripten/cache"
export EM_FROZEN_CACHE=1
export PATH="$sdk/emscripten:$sdk/bin:$(pack Python)/bin:$PATH"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

git clone --quiet --depth 1 --branch "$KTX_TAG" https://github.com/KhronosGroup/KTX-Software.git "$work/src"

flags="-fwasm-exceptions"
emcmake cmake -S "$work/src" -B "$work/build" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_C_FLAGS="$flags" \
    -DCMAKE_CXX_FLAGS="$flags" \
    -DBUILD_SHARED_LIBS=OFF \
    -DKTX_FEATURE_TESTS=OFF \
    -DKTX_FEATURE_TOOLS=OFF \
    -DKTX_FEATURE_DOC=OFF \
    -DKTX_FEATURE_GL_UPLOAD=OFF \
    -DKTX_FEATURE_ETC_UNPACK=OFF \
    -DBASISU_SUPPORT_SSE=OFF
cmake --build "$work/build" --target ktx

# libktx's writer entry points (bound by Ktx2.NET) link against the ASTC encoder archive; fold
# both into the single archive the relink references. An MRI script rather than extract-and-add
# keeps members whose basenames collide. The linker still pulls only the objects the pinvoke
# table reaches.
mkdir -p "$(dirname "$output")"
rm -f "$output"
{
    echo "create $output"
    for archive in "$work/build/libktx.a" $(find "$work/build" -name 'libastcenc*.a'); do
        echo "addlib $archive"
    done
    echo "save"
    echo "end"
} | llvm-ar -M
llvm-ranlib "$output"
echo "wrote $output ($(wc -c <"$output") bytes)"
