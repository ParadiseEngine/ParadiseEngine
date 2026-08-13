#!/usr/bin/env bash
# Build libParadiseWwise — the native half of Paradise.Audio.Wwise.
#
# WHY A SHELL SCRIPT AND NOT CMAKE. This is six translation units and one fixed link line. CMake
# would add a toolchain every contributor must install to build the engine, in exchange for
# nothing this needs. If a second platform or a real dependency graph ever shows up, revisit.
#
# WHY IT IS NOT COMMITTED AS A BINARY. The engine repo is MIT-licensed and published to GitHub;
# the Wwise SDK is commercial and its libraries cannot be redistributed. So the dylib is built
# from the DEVELOPER'S OWN install, on their machine, and never checked in. `Wwise.targets`
# invokes this script and degrades to a warning when no SDK is found.
#
# Usage: build.sh --out <dir> [--config Profile|Release|Debug] [--sdk <path>]
#
# Configurations are not interchangeable:
#   Debug    asserts on, slow
#   Profile  optimized, and the ONLY one Wwise Authoring's profiler can attach to
#   Release  optimized, AK_OPTIMIZED, comms compiled out entirely
# Profile is the default: attaching the profiler is worth far more during development than the
# marginal speed of Release, and audio is not what the frame budget is spent on.

set -euo pipefail

OUT_DIR=""
CONFIG="Profile"
SDK_ROOT="${WWISESDK:-}"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --out)    OUT_DIR="$2"; shift 2 ;;
        --config) CONFIG="$2";  shift 2 ;;
        --sdk)    SDK_ROOT="$2"; shift 2 ;;
        *) echo "build.sh: unknown argument '$1'" >&2; exit 2 ;;
    esac
done

if [[ -z "$OUT_DIR" ]]; then
    echo "build.sh: --out <dir> is required" >&2
    exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ---- locate the SDK ---------------------------------------------------------------------------

if [[ -z "$SDK_ROOT" ]]; then
    # Newest install wins. `sort -V` so Wwise_2026.1.2 sorts above Wwise_2025.1.7 rather than
    # lexicographically, where "2025" and "2026" happen to work but a two-digit minor would not.
    SDK_ROOT="$(ls -d /Applications/Audiokinetic/Wwise_*/SDK 2>/dev/null | sort -V | tail -1 || true)"
fi

if [[ -z "$SDK_ROOT" || ! -f "$SDK_ROOT/include/AK/SoundEngine/Common/AkSoundEngine.h" ]]; then
    echo "build.sh: Wwise SDK not found. Set WWISESDK or pass --sdk <path>/SDK." >&2
    echo "          Install the SDK for your platform via the Audiokinetic Launcher." >&2
    exit 3
fi

# ---- platform ---------------------------------------------------------------------------------

case "$(uname -s)" in
    Darwin)
        # The Xcode-versioned directory changes with each SDK release; take the newest present.
        LIB_PLATFORM="$(ls -d "$SDK_ROOT"/Mac_Xcode* 2>/dev/null | sort -V | tail -1 || true)"
        STREAM_PLATFORM="Mac"
        ARCH_FLAGS=(-arch arm64 -arch x86_64)
        # AVFoundation is not optional: Wwise's macOS sink (AkAVAudioEngineSink) lives in
        # libAkSoundEngine.a and references AVAudioEngine directly, so omitting it fails the
        # link with undefined ObjC classes rather than anything mentioning audio.
        FRAMEWORKS=(-framework AudioToolbox -framework AudioUnit -framework CoreAudio
                    -framework CoreFoundation -framework Foundation -framework AVFoundation)
        LIB_EXT="dylib"
        SHARED_FLAG="-dynamiclib"
        ;;
    Linux)
        LIB_PLATFORM="$SDK_ROOT/Linux_x64"
        STREAM_PLATFORM="POSIX"
        ARCH_FLAGS=()
        FRAMEWORKS=()
        LIB_EXT="so"
        SHARED_FLAG="-shared"
        ;;
    *)
        echo "build.sh: unsupported platform '$(uname -s)'. Windows needs an MSVC path here." >&2
        exit 3
        ;;
esac

LIB_DIR="$LIB_PLATFORM/$CONFIG/lib"
if [[ ! -d "$LIB_DIR" ]]; then
    echo "build.sh: Wwise $CONFIG libraries not found at $LIB_DIR" >&2
    exit 3
fi

# ---- sources ----------------------------------------------------------------------------------
#
# The low-level I/O hook ships as SOURCE, not a library — Wwise does that deliberately so an
# integration owns the instance. We compile the deferred POSIX variant, which is what the stream
# manager wants for streamed media.

IO_HOOK="$SDK_ROOT/source/StreamManager/DefaultIOHook"
IO_HOOK_PLATFORM="$IO_HOOK/POSIX"

SOURCES=(
    "$SCRIPT_DIR/ParadiseWwise.cpp"
    "$IO_HOOK/Common/AkBaseLowLevelIOHook.cpp"
    "$IO_HOOK/Common/AkFilePackage.cpp"
    "$IO_HOOK/Common/AkFilePackageLUT.cpp"
    "$IO_HOOK/Common/AkGeneratedSoundBanksResolver.cpp"
    "$IO_HOOK_PLATFORM/AkDefaultIOHook.cpp"
)

# source/StreamManager/{Common,$platform} are on the include path for stdafx.h and
# AkPlatformStreamingDefaults.h, which the hook sources include by bare name.
INCLUDES=(
    -I"$SCRIPT_DIR"
    -I"$SDK_ROOT/include"
    -I"$IO_HOOK/Common"
    -I"$IO_HOOK_PLATFORM"
    -I"$SDK_ROOT/source/StreamManager/Common"
    -I"$SDK_ROOT/source/StreamManager/$STREAM_PLATFORM"
)

# 2026 notes worth keeping: the music engine is folded into AkSoundEngine (no libAkMusicEngine
# any more), and Spatial Audio is now libAkAcoustics — deliberately not linked, since rooms and
# portals are out of scope for this integration.
#
# Codecs and effects must be linked AND referenced from ParadiseWwise.cpp (see the plug-in
# registration block there): they register themselves from static initializers, and a linker
# discards any object file in a .a that nothing references. Linking without the factory include
# produces a binary that loads banks fine and then cannot decode or process a thing.
LIBS=(
    -lAkSoundEngine -lAkMemoryMgr -lAkStreamMgr
    # Codecs — mandatory, banks are encoded with these.
    -lAkVorbisDecoder -lAkOpusDecoder
    # Stock effects. Linked wholesale because which ones a sound designer uses is decided in the
    # authoring tool, long after this is built.
    -lAkCompressorFX -lAkDelayFX -lAkFlangerFX -lAkGainFX
    -lAkGuitarDistortionFX -lAkHarmonizerFX -lAkMatrixReverbFX -lAkMeterFX
    -lAkParametricEQFX -lAkPeakLimiterFX -lAkPitchShifterFX -lAkRoomVerbFX
    -lAkStereoDelayFX -lAkTimeStretchFX -lAkTremoloFX
    # Stock sources.
    -lAkSilenceSource -lAkSineSource -lAkSynthOneSource -lAkToneSource -lAkAudioInputSource
)

DEFINES=()
if [[ "$CONFIG" == "Release" ]]; then
    DEFINES+=(-DAK_OPTIMIZED)
else
    # The profiler transport. Present only in Debug/Profile — Release strips comms at compile
    # time, which is exactly why a Release build cannot be profiled.
    LIBS+=(-lCommunicationCentral)
fi

# ---- build ------------------------------------------------------------------------------------

OUTPUT="$OUT_DIR/libParadiseWwise.$LIB_EXT"
mkdir -p "$OUT_DIR"

# Skip the (slow, universal-binary) rebuild when nothing changed. The comparison is against our
# own source only: the SDK is versioned into the output directory by the caller, so a different
# SDK is a different path rather than a stale artifact.
if [[ -f "$OUTPUT" && "$OUTPUT" -nt "$SCRIPT_DIR/ParadiseWwise.cpp" && "$OUTPUT" -nt "$SCRIPT_DIR/ParadiseWwise.h" ]]; then
    echo "libParadiseWwise: up to date ($OUTPUT)"
    exit 0
fi

echo "libParadiseWwise: building $CONFIG from $SDK_ROOT"

# The ${arr[@]+"${arr[@]}"} guard is not noise: macOS ships bash 3.2, where expanding an EMPTY
# array under `set -u` is an "unbound variable" error. ARCH_FLAGS, DEFINES and FRAMEWORKS are all
# legitimately empty on some configuration/platform combination.
clang++ -std=c++17 -O2 -fvisibility=hidden "$SHARED_FLAG" \
    ${ARCH_FLAGS[@]+"${ARCH_FLAGS[@]}"} \
    -o "$OUTPUT" \
    "${SOURCES[@]}" \
    "${INCLUDES[@]}" \
    ${DEFINES[@]+"${DEFINES[@]}"} \
    -L"$LIB_DIR" \
    "${LIBS[@]}" \
    ${FRAMEWORKS[@]+"${FRAMEWORKS[@]}"}

echo "libParadiseWwise: wrote $OUTPUT"
