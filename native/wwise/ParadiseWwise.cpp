#include "ParadiseWwise.h"

#include <math.h>

#include <AK/SoundEngine/Common/AkMemoryMgr.h>
#include <AK/SoundEngine/Common/AkMemoryMgrModule.h>
#include <AK/SoundEngine/Common/AkOption.h>
#include <AK/SoundEngine/Common/AkOptionTypes.h>
#include <AK/SoundEngine/Common/AkSoundEngine.h>
#include <AK/SoundEngine/Common/AkStreamMgrModule.h>
#include <AK/SoundEngine/Common/AkTypes.h>

// The default streaming I/O hook, compiled from the SDK's own sources (see build.sh).
// Wwise ships it as source rather than a library precisely so integrations can own the instance.
#include <AkFilePackageLowLevelIODeferred.h>

// ---- plug-in registration ----------------------------------------------------------------------
//
// THESE INCLUDES ARE NOT OPTIONAL AND THEY ARE NOT DOCUMENTATION. Wwise's codecs and effects live
// in static libraries and register themselves from static initializers. A linker drops any object
// file in a .a that nothing references, so linking libAkVorbisDecoder.a alone achieves nothing —
// the registration never makes it into the binary. Each *Factory.h below emits the reference that
// forces its object file to be kept.
//
// The failure mode is why this comment is long: everything links, the engine initializes, banks
// load, events post and return valid playing ids, and then nothing is audible except
// "Codec plug-in not registered" on stderr. It reads as a broken integration rather than a
// missing link-time reference.
//
// Codecs are mandatory — a bank encoded in Vorbis or Opus cannot be decoded without them.
#include <AK/Plugin/AkVorbisDecoderFactory.h>
#include <AK/Plugin/AkOpusDecoderFactory.h>

// The stock Audiokinetic effects. Included wholesale rather than à la carte because the set a
// sound designer reaches for is decided in the authoring tool, long after this is compiled: an
// effect they add and we did not link is silent at runtime with only a numeric plug-in id to
// diagnose it. They cost binary size and nothing at runtime until an authored effect uses one.
#include <AK/Plugin/AkCompressorFXFactory.h>
#include <AK/Plugin/AkDelayFXFactory.h>
#include <AK/Plugin/AkFlangerFXFactory.h>
#include <AK/Plugin/AkGainFXFactory.h>
#include <AK/Plugin/AkGuitarDistortionFXFactory.h>
#include <AK/Plugin/AkHarmonizerFXFactory.h>
#include <AK/Plugin/AkMatrixReverbFXFactory.h>
#include <AK/Plugin/AkMeterFXFactory.h>
#include <AK/Plugin/AkParametricEQFXFactory.h>
#include <AK/Plugin/AkPeakLimiterFXFactory.h>
#include <AK/Plugin/AkPitchShifterFXFactory.h>
#include <AK/Plugin/AkRoomVerbFXFactory.h>
#include <AK/Plugin/AkStereoDelayFXFactory.h>
#include <AK/Plugin/AkTimeStretchFXFactory.h>
#include <AK/Plugin/AkTremoloFXFactory.h>

// Stock sources. SilenceSource in particular is used by authored containers more often than its
// name suggests (as a placeholder and for timed gaps).
#include <AK/Plugin/AkSilenceSourceFactory.h>
#include <AK/Plugin/AkSineSourceFactory.h>
#include <AK/Plugin/AkSynthOneSourceFactory.h>
#include <AK/Plugin/AkToneSourceFactory.h>
#include <AK/Plugin/AkAudioInputSourceFactory.h>

namespace
{
    // The one streaming hook for the process. Wwise holds the pointer for the sound engine's
    // whole lifetime, so this must outlive Init/Term — a stack or heap instance owned by Init
    // would have to be kept alive by hand for no benefit.
    CAkFilePackageLowLevelIODeferred g_lowLevelIO;

    bool g_initialized = false;

    /// Wwise expects orientation vectors that are unit length and mutually orthogonal, and
    /// REJECTS SetPosition outright otherwise. A rejected position is not audible as an error —
    /// the emitter simply stays where it last was — so the failure reads as "3D audio is broken"
    /// rather than "one vector was denormalized". Repairing it here means no caller can trip it.
    ///
    /// Top is re-derived from front rather than trusted: right = top x front, then top =
    /// front x right restores orthogonality with the smallest change to the caller's intent.
    void Orthonormalize(AkVector& io_front, AkVector& io_top)
    {
        auto normalize = [](AkVector& v, float fx, float fy, float fz) {
            const float lengthSq = v.X * v.X + v.Y * v.Y + v.Z * v.Z;
            if (lengthSq < 1e-8f)
            {
                v.X = fx; v.Y = fy; v.Z = fz;
                return;
            }
            const float inv = 1.0f / sqrtf(lengthSq);
            v.X *= inv; v.Y *= inv; v.Z *= inv;
        };

        normalize(io_front, 0.0f, 0.0f, 1.0f);
        normalize(io_top, 0.0f, 1.0f, 0.0f);

        AkVector right;
        right.X = io_top.Y * io_front.Z - io_top.Z * io_front.Y;
        right.Y = io_top.Z * io_front.X - io_top.X * io_front.Z;
        right.Z = io_top.X * io_front.Y - io_top.Y * io_front.X;

        const float rightLengthSq = right.X * right.X + right.Y * right.Y + right.Z * right.Z;
        if (rightLengthSq < 1e-8f)
        {
            // front and top are parallel — the caller's top is unusable. Pick any axis not
            // aligned with front and rebuild from that.
            io_top.X = fabsf(io_front.Y) > 0.9f ? 1.0f : 0.0f;
            io_top.Y = fabsf(io_front.Y) > 0.9f ? 0.0f : 1.0f;
            io_top.Z = 0.0f;
            right.X = io_top.Y * io_front.Z - io_top.Z * io_front.Y;
            right.Y = io_top.Z * io_front.X - io_top.X * io_front.Z;
            right.Z = io_top.X * io_front.Y - io_top.Y * io_front.X;
        }

        normalize(right, 1.0f, 0.0f, 0.0f);

        io_top.X = io_front.Y * right.Z - io_front.Z * right.Y;
        io_top.Y = io_front.Z * right.X - io_front.X * right.Z;
        io_top.Z = io_front.X * right.Y - io_front.Y * right.X;
        normalize(io_top, 0.0f, 1.0f, 0.0f);
    }
}

// ---- lifecycle --------------------------------------------------------------------------------

extern "C" int32_t Pdx_Wwise_Init(
    const char* in_soundBankPath, int32_t in_enableProfiler, int32_t in_useSubfoldering)
{
    if (g_initialized)
    {
        return AK_AlreadyInitialized;
    }

    // 1. Memory manager. Everything below allocates through it, so it is strictly first.
    AKRESULT result = AK::MemoryMgr::Init(nullptr);
    if (result != AK_Success)
    {
        return result;
    }

    // 2. Hand Wwise the streaming hook BEFORE Init. In 2026 the stream manager and its device are
    //    created inside AK::SoundEngine::Init from the option bag — there is no longer a separate
    //    AK::StreamMgr::Create()/CreateDevice() call for an integration to make. An Init with no
    //    hook set brings the engine up successfully and then fails every bank load.
    AK::Option::SetP(AkOption_StreamMgr_LowLevelIOHook, &g_lowLevelIO);

    //    Tell the hook which on-disk layout to expect. Auto-defined SoundBanks put banks under
    //    Event/ and Bus/ and loose media under Media/; without this the hook looks for every
    //    .wem directly in the base path, banks still load, and every voice fails with
    //    "Media <id> was not loaded for this source" — audible as total silence.
    g_lowLevelIO.SetUseSubfoldering(in_useSubfoldering != 0);

    // 3. Profiler. Also an option in 2026 — AK::Comm has no Init of its own; the module is brought
    //    up as part of SoundEngine::Init when this is set. Compiled out entirely in AK_OPTIMIZED
    //    builds, where the option key does not exist.
#ifndef AK_OPTIMIZED
    AK::Option::SetI(AkOption_Comm_Enable, in_enableProfiler != 0 ? 1 : 0);
    if (in_enableProfiler != 0)
    {
        AK::Option::SetS(AkOption_Comm_AppNetworkName, "Paradise");
    }
#else
    (void)in_enableProfiler;
#endif

    result = AK::SoundEngine::Init();
    if (result != AK_Success)
    {
        AK::MemoryMgr::Term();
        return result;
    }

    // 4. Where banks are resolved from. After Init, because it forwards to the hook the stream
    //    manager now owns.
    if (in_soundBankPath != nullptr && in_soundBankPath[0] != '\0')
    {
        result = AK::StreamMgr::AddBasePath(in_soundBankPath);
        if (result != AK_Success)
        {
            AK::SoundEngine::Term();
            AK::MemoryMgr::Term();
            return result;
        }
    }

    g_initialized = true;
    return AK_Success;
}

extern "C" void Pdx_Wwise_Term(void)
{
    if (!g_initialized)
    {
        return;
    }

    AK::SoundEngine::Term();
    AK::MemoryMgr::Term();
    g_initialized = false;
}

extern "C" int32_t Pdx_Wwise_IsInitialized(void)
{
    return g_initialized && AK::SoundEngine::IsInitialized() ? 1 : 0;
}

extern "C" int32_t Pdx_Wwise_RenderAudio(void)
{
    return AK::SoundEngine::RenderAudio();
}

// ---- soundbanks -------------------------------------------------------------------------------

extern "C" int32_t Pdx_Wwise_LoadBank(const char* in_bankName, uint32_t* out_bankId)
{
    AkBankID bankId = 0;
    const AKRESULT result = AK::SoundEngine::LoadBank(in_bankName, bankId);
    if (out_bankId != nullptr)
    {
        *out_bankId = bankId;
    }
    return result;
}

extern "C" int32_t Pdx_Wwise_UnloadBank(const char* in_bankName)
{
    return AK::SoundEngine::UnloadBank(in_bankName, nullptr);
}

// ---- game objects -----------------------------------------------------------------------------

extern "C" int32_t Pdx_Wwise_RegisterGameObj(uint64_t in_gameObject, const char* in_name)
{
    return in_name != nullptr
        ? AK::SoundEngine::RegisterGameObj(in_gameObject, in_name)
        : AK::SoundEngine::RegisterGameObj(in_gameObject);
}

extern "C" int32_t Pdx_Wwise_UnregisterGameObj(uint64_t in_gameObject)
{
    return AK::SoundEngine::UnregisterGameObj(in_gameObject);
}

extern "C" int32_t Pdx_Wwise_SetPosition(
    uint64_t in_gameObject,
    float in_posX, float in_posY, float in_posZ,
    float in_frontX, float in_frontY, float in_frontZ,
    float in_topX, float in_topY, float in_topZ)
{
    AkVector front; front.X = in_frontX; front.Y = in_frontY; front.Z = in_frontZ;
    AkVector top;   top.X = in_topX;     top.Y = in_topY;     top.Z = in_topZ;
    Orthonormalize(front, top);

    AkVector position; position.X = in_posX; position.Y = in_posY; position.Z = in_posZ;

    AkSoundPosition soundPosition;
    soundPosition.Set(position, front, top);
    return AK::SoundEngine::SetPosition(in_gameObject, soundPosition);
}

extern "C" int32_t Pdx_Wwise_SetDefaultListener(uint64_t in_gameObject)
{
    const AkGameObjectID listener = in_gameObject;
    return AK::SoundEngine::SetDefaultListeners(&listener, 1);
}

// ---- playback ---------------------------------------------------------------------------------

extern "C" uint32_t Pdx_Wwise_PostEvent(uint32_t in_eventId, uint64_t in_gameObject)
{
    return AK::SoundEngine::PostEvent(in_eventId, in_gameObject);
}

extern "C" void Pdx_Wwise_StopPlayingID(uint32_t in_playingId, int32_t in_fadeOutMs)
{
    AK::SoundEngine::ExecuteActionOnPlayingID(
        AkActionOnEventType_Stop, in_playingId, in_fadeOutMs);
}

extern "C" void Pdx_Wwise_StopAll(uint64_t in_gameObject)
{
    AK::SoundEngine::StopAll(in_gameObject);
}

// ---- parameters -------------------------------------------------------------------------------

extern "C" int32_t Pdx_Wwise_SetRTPCValue(
    uint32_t in_rtpcId, float in_value, uint64_t in_gameObject)
{
    return AK::SoundEngine::SetRTPCValue(in_rtpcId, in_value, in_gameObject);
}

extern "C" int32_t Pdx_Wwise_SetSwitch(
    uint32_t in_switchGroup, uint32_t in_switchState, uint64_t in_gameObject)
{
    return AK::SoundEngine::SetSwitch(in_switchGroup, in_switchState, in_gameObject);
}

extern "C" int32_t Pdx_Wwise_SetState(uint32_t in_stateGroup, uint32_t in_state)
{
    return AK::SoundEngine::SetState(in_stateGroup, in_state);
}

// ---- ids --------------------------------------------------------------------------------------

extern "C" uint32_t Pdx_Wwise_GetIDFromString(const char* in_name)
{
    return AK::SoundEngine::GetIDFromString(in_name);
}
