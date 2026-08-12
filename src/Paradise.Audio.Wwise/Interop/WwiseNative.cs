using System.Runtime.InteropServices;

namespace Paradise.Audio.Wwise.Interop;

/// <summary>
/// P/Invoke declarations for <c>libParadiseWwise</c>, the native shim in
/// <c>ParadiseEngine/native/wwise</c>. One method per shim export, no policy — everything that
/// makes a decision lives in the wrapper types.
///
/// The library is built from the developer's own Wwise SDK by <c>Wwise.targets</c> and staged
/// next to the host assembly. It is legitimately ABSENT on a machine with no Wwise install, so
/// every caller must be prepared for <see cref="DllNotFoundException"/> on the first call —
/// <see cref="WwiseSoundEngine.TryInitialize"/> is the one place that catches it.
/// </summary>
internal static partial class WwiseNative
{
    private const string Library = "ParadiseWwise";

    /// <summary>AKRESULT's success value. Everything else is a failure worth reporting verbatim,
    /// so the code is surfaced rather than collapsed to a bool.</summary>
    public const int Success = 1;

    /// <summary>AK_INVALID_GAME_OBJECT — the global scope for RTPCs.</summary>
    public const ulong GlobalObject = ulong.MaxValue;

    /// <summary>AK_INVALID_PLAYING_ID / AK_INVALID_UNIQUE_ID.</summary>
    public const uint InvalidId = 0u;

    // ---- lifecycle ----------------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "Pdx_Wwise_Init", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Init(string soundBankPath, int enableProfiler, int useSubfoldering);

    [LibraryImport(Library, EntryPoint = "Pdx_Wwise_Term")]
    public static partial void Term();

    [LibraryImport(Library, EntryPoint = "Pdx_Wwise_IsInitialized")]
    public static partial int IsInitialized();

    [LibraryImport(Library, EntryPoint = "Pdx_Wwise_RenderAudio")]
    public static partial int RenderAudio();

    // ---- soundbanks ---------------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "Pdx_Wwise_LoadBank", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int LoadBank(string bankName, out uint bankId);

    [LibraryImport(Library, EntryPoint = "Pdx_Wwise_UnloadBank", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int UnloadBank(string bankName);

    // ---- game objects -------------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "Pdx_Wwise_RegisterGameObj", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int RegisterGameObj(ulong gameObject, string? name);

    [LibraryImport(Library, EntryPoint = "Pdx_Wwise_UnregisterGameObj")]
    public static partial int UnregisterGameObj(ulong gameObject);

    [LibraryImport(Library, EntryPoint = "Pdx_Wwise_SetPosition")]
    public static partial int SetPosition(
        ulong gameObject,
        float posX, float posY, float posZ,
        float frontX, float frontY, float frontZ,
        float topX, float topY, float topZ);

    [LibraryImport(Library, EntryPoint = "Pdx_Wwise_SetDefaultListener")]
    public static partial int SetDefaultListener(ulong gameObject);

    // ---- playback -----------------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "Pdx_Wwise_PostEvent")]
    public static partial uint PostEvent(uint eventId, ulong gameObject);

    [LibraryImport(Library, EntryPoint = "Pdx_Wwise_StopPlayingID")]
    public static partial void StopPlayingId(uint playingId, int fadeOutMs);

    [LibraryImport(Library, EntryPoint = "Pdx_Wwise_StopAll")]
    public static partial void StopAll(ulong gameObject);

    // ---- parameters ---------------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "Pdx_Wwise_SetRTPCValue")]
    public static partial int SetRtpcValue(uint rtpcId, float value, ulong gameObject);

    [LibraryImport(Library, EntryPoint = "Pdx_Wwise_SetSwitch")]
    public static partial int SetSwitch(uint switchGroup, uint switchState, ulong gameObject);

    [LibraryImport(Library, EntryPoint = "Pdx_Wwise_SetState")]
    public static partial int SetState(uint stateGroup, uint state);

    // ---- ids ----------------------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "Pdx_Wwise_GetIDFromString", StringMarshalling = StringMarshalling.Utf8)]
    public static partial uint GetIdFromString(string name);
}
