// Flat C surface over the Wwise sound engine, for P/Invoke from Paradise.Audio.Wwise.
//
// WHY THIS EXISTS. Wwise ships C++ static libraries. Wwise 2026 does expose a partial flat-C API
// (AK_SoundEngine_Init, AK_CommandBuffer_*, AK_Option_Set*), but three things a real integration
// needs are C++-only and cannot be reached from C#:
//
//   - AK::SoundEngine::LoadBank      — no C alias, and no AkCommand for it either
//   - AK::StreamMgr::IAkLowLevelIOHook — an interface; the engine cannot stream a soundbank
//                                        without an instance, and instantiating one means C++
//   - AK::Comm                       — profiler connection, options-driven but C++-declared
//
// So this library links Wwise, owns the one IO hook instance, and re-exports everything the
// managed side needs as `extern "C"`. It deliberately stays a thin translation layer: no caching,
// no state beyond the hook, no policy. Everything above (game objects, event ids, tick order)
// belongs in managed code where it can be tested.
//
// THREAD AFFINITY. Wwise's API is thread-safe, but this library assumes a single caller thread
// for the whole lifecycle — Paradise drives it from the sim thread, which is where the published
// world state it reads already lives.

#ifndef PARADISE_WWISE_H_
#define PARADISE_WWISE_H_

#include <stdint.h>

#if defined(_WIN32)
#define PDX_WWISE_API __declspec(dllexport)
#else
#define PDX_WWISE_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

// Mirrors AKRESULT for the values callers act on. Any other AKRESULT is passed through as its
// raw integer, so an unexpected failure is still diagnosable from managed code.
#define PDX_WWISE_SUCCESS 1

// Sentinel matching AK_INVALID_PLAYING_ID / AK_INVALID_UNIQUE_ID.
#define PDX_WWISE_INVALID_ID 0u

// Matches AK_INVALID_GAME_OBJECT. Pass it where a call accepts a game object to mean
// "global scope" — an RTPC set here applies to every object that has no value of its own.
#define PDX_WWISE_GLOBAL_OBJECT ((uint64_t)-1)

// ---- lifecycle ------------------------------------------------------------------------------

/// Initialize the memory manager, stream manager, low-level I/O and sound engine.
///
/// in_soundBankPath is the directory soundbanks are resolved against (the generated Mac bank
/// folder). in_enableProfiler opens the Wwise Authoring profiler ports; it is a no-op in a
/// Release (AK_OPTIMIZED) build of this library, where Wwise strips comms entirely.
///
/// in_useSubfoldering selects the on-disk LAYOUT of that folder, and it is not cosmetic:
///   0 — flat: every .bnk and .wem sits directly in the folder.
///   1 — the layout Wwise emits for auto-defined SoundBanks: banks under Event/ and Bus/, loose
///       media under Media/. Pass BARE file names for banks in this mode; the resolver supplies
///       the subfolder itself.
/// Choosing wrong is not a load failure — banks load either way — but every sound then reports
/// "Media <id> was not loaded for this source" and nothing is audible.
/// Returns AKRESULT.
PDX_WWISE_API int32_t Pdx_Wwise_Init(
    const char* in_soundBankPath, int32_t in_enableProfiler, int32_t in_useSubfoldering);

/// Terminate everything Init brought up, in reverse order. Safe to call when not initialized.
PDX_WWISE_API void Pdx_Wwise_Term(void);

PDX_WWISE_API int32_t Pdx_Wwise_IsInitialized(void);

/// Process the frame's queued commands. Must be called once per frame or nothing is ever heard.
PDX_WWISE_API int32_t Pdx_Wwise_RenderAudio(void);

// ---- soundbanks -----------------------------------------------------------------------------

/// Load a bank by file name (e.g. "Init.bnk"). Blocking. Returns AKRESULT; the bank id is
/// written to out_bankId when non-null, which is what UnloadBankById needs.
PDX_WWISE_API int32_t Pdx_Wwise_LoadBank(const char* in_bankName, uint32_t* out_bankId);

PDX_WWISE_API int32_t Pdx_Wwise_UnloadBank(const char* in_bankName);

// ---- game objects ---------------------------------------------------------------------------

/// Register an emitter/listener. in_name is profiler-only and may be null.
PDX_WWISE_API int32_t Pdx_Wwise_RegisterGameObj(uint64_t in_gameObject, const char* in_name);

PDX_WWISE_API int32_t Pdx_Wwise_UnregisterGameObj(uint64_t in_gameObject);

/// Position an object. The orientation vectors must be unit length and orthogonal — Wwise
/// rejects the call outright otherwise, which is a silent-audio bug that is hard to spot from
/// the game side, so the managed wrapper normalizes before calling.
PDX_WWISE_API int32_t Pdx_Wwise_SetPosition(
    uint64_t in_gameObject,
    float in_posX, float in_posY, float in_posZ,
    float in_frontX, float in_frontY, float in_frontZ,
    float in_topX, float in_topY, float in_topZ);

/// Make in_gameObject the default listener for every object that has not set its own.
PDX_WWISE_API int32_t Pdx_Wwise_SetDefaultListener(uint64_t in_gameObject);

// ---- playback -------------------------------------------------------------------------------

/// Post an event by id. Returns an AkPlayingID, or PDX_WWISE_INVALID_ID on failure — note this
/// is the opposite convention from the AKRESULT-returning calls above.
PDX_WWISE_API uint32_t Pdx_Wwise_PostEvent(uint32_t in_eventId, uint64_t in_gameObject);

/// Stop one posted instance, fading out over in_fadeOutMs.
PDX_WWISE_API void Pdx_Wwise_StopPlayingID(uint32_t in_playingId, int32_t in_fadeOutMs);

PDX_WWISE_API void Pdx_Wwise_StopAll(uint64_t in_gameObject);

// ---- parameters -----------------------------------------------------------------------------

/// Set an RTPC. Pass PDX_WWISE_GLOBAL_OBJECT as in_gameObject for the global scope.
PDX_WWISE_API int32_t Pdx_Wwise_SetRTPCValue(
    uint32_t in_rtpcId, float in_value, uint64_t in_gameObject);

PDX_WWISE_API int32_t Pdx_Wwise_SetSwitch(
    uint32_t in_switchGroup, uint32_t in_switchState, uint64_t in_gameObject);

PDX_WWISE_API int32_t Pdx_Wwise_SetState(uint32_t in_stateGroup, uint32_t in_state);

// ---- ids ------------------------------------------------------------------------------------

/// Hash a name to the id Wwise generated for it. Same FNV hash the authoring tool uses, so this
/// agrees with Wwise_IDs.h by construction.
PDX_WWISE_API uint32_t Pdx_Wwise_GetIDFromString(const char* in_name);

#ifdef __cplusplus
}
#endif

#endif // PARADISE_WWISE_H_
