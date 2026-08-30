#!/usr/bin/env bash
# Idempotent Cloud Agent setup for the Paradise Engine .NET 10 monorepo.
# Safe to run repeatedly: system packages, the SDK, and the wasm workload are
# only installed when missing, and the restore/build steps are incremental.
set -euo pipefail

DOTNET_CHANNEL="10.0"
DOTNET_DIR="$HOME/.dotnet"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# --- System packages -------------------------------------------------------
# clang + zlib1g-dev : NativeAOT publish path (Paradise.BT.Sample).
# libc++1/libc++abi1 : runtime deps of the Dawn native lib (webgpu_dawn.so).
# mesa-vulkan-drivers: Mesa's software Vulkan (lavapipe) so the WebGPU backend
#                      and its tests run without a physical GPU.
# vulkan loader/tools: Vulkan ICD loader + vulkaninfo for diagnostics.
sudo apt-get update -qq
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    clang zlib1g-dev libc++1 libc++abi1 \
    mesa-vulkan-drivers libvulkan1 vulkan-tools \
    curl ca-certificates git

# --- .NET SDK 10 -----------------------------------------------------------
# global.json pins 10.0.200 with rollForward=latestMinor, so the latest 10.0
# feature band satisfies it.
if [ ! -x "$DOTNET_DIR/dotnet" ]; then
    curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    chmod +x /tmp/dotnet-install.sh
    /tmp/dotnet-install.sh --channel "$DOTNET_CHANNEL" --install-dir "$DOTNET_DIR"
fi
export DOTNET_ROOT="$DOTNET_DIR"
export PATH="$DOTNET_DIR:$DOTNET_DIR/tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# --- WebAssembly workload --------------------------------------------------
# Paradise.Rendering.Browser.Sample is a Microsoft.NET.Sdk.WebAssembly app, so
# the full solution build needs wasm-tools (otherwise NETSDK1147). Idempotent.
dotnet workload install wasm-tools

# --- Shell environment -----------------------------------------------------
# Persisted so every future shell (login and interactive) finds the SDK and
# uses the software Vulkan driver.
sudo tee /etc/profile.d/paradise-dotnet.sh >/dev/null <<'EOF'
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
# Force Mesa's software Vulkan (lavapipe) so the WebGPU/Dawn backend works
# without a physical GPU. Drop this to prefer a real adapter where one exists.
export VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/tmp/xdg-runtime}"
mkdir -p "$XDG_RUNTIME_DIR" 2>/dev/null || true
EOF

if ! grep -q 'paradise-dotnet.sh' "$HOME/.bashrc" 2>/dev/null; then
    echo '[ -f /etc/profile.d/paradise-dotnet.sh ] && . /etc/profile.d/paradise-dotnet.sh' >> "$HOME/.bashrc"
fi

# --- Restore + build -------------------------------------------------------
# Warms the NuGet cache, downloads the pinned slangc toolchain, and compiles
# every project so the first agent command starts from a built tree.
cd "$REPO_ROOT"
dotnet restore ParadiseEngine.slnx
dotnet build ParadiseEngine.slnx --no-restore
