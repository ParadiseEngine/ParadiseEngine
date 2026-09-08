#!/usr/bin/env bash
# Build the native Paradise.Audio.Wwise shim from the developer's Wwise SDK.
# Wwise libraries are commercial and must not be committed to this MIT repository.
# Wwise.targets invokes this script and warns when the SDK is unavailable.
# Usage: build.sh --out <dir> [--config Profile|Release|Debug] [--sdk <path>]
# Debug enables assertions; Profile is optimized with profiler support (default);
# Release defines AK_OPTIMIZED and excludes profiler communication.

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

# Wwise 2026 includes music in AkSoundEngine; spatial audio (AkAcoustics) is unused.
# Keep codec/effect libraries and their Factory.h references together: static registration
# objects are otherwise discarded by the linker and playback produces no audio.
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

# Bash 3.2 treats empty arrays as unbound under set -u; preserve these guarded expansions.
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
