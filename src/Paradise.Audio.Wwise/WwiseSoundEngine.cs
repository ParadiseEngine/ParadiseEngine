using System.Numerics;
using Paradise.Audio.Wwise.Interop;

namespace Paradise.Audio.Wwise;

/// <summary>
/// The Wwise sound engine's lifetime and its whole API surface, as one object.
///
/// AUDIO IS NEVER LOAD-BEARING. Every method here degrades instead of throwing: on a machine with
/// no Wwise SDK the native library does not exist, <see cref="TryInitialize"/> returns false, and
/// every subsequent call is a cheap no-op. A game must be playable in silence — a contributor
/// without a Wwise licence, and CI, both have to be able to run it. That is why this type reports
/// failure through return values and <see cref="LastError"/> rather than exceptions.
///
/// THREAD AFFINITY. Wwise's own API is thread-safe, but this type assumes ONE caller thread for
/// its whole lifetime, which is also what the native shim assumes. Paradise hosts drive it from
/// the sim thread, where the published world state it reads already lives. Calling
/// <see cref="RenderAudio"/> from the render thread while the sim thread posts events would work
/// in Wwise's terms and still be a mistake here — the ordering between a position update and the
/// event that depends on it would stop being deterministic.
/// </summary>
public sealed class WwiseSoundEngine : IDisposable
{
    private bool _initialized;
    private bool _disposed;

    /// <summary>AKRESULT of the last call that failed, or 0. Diagnostic only — look it up in
    /// AkTypes.h. Not cleared by a subsequent success, so it answers "what went wrong" rather
    /// than "is anything wrong right now".</summary>
    public int LastError { get; private set; }

    /// <summary>True when the sound engine is up and calls will actually reach it.</summary>
    public bool IsInitialized => _initialized;

    /// <summary>
    /// Bring up the memory manager, streaming, and the sound engine, resolving soundbanks against
    /// <paramref name="soundBankPath"/>.
    ///
    /// <paramref name="enableProfiler"/> opens the ports Wwise Authoring's profiler connects to.
    /// It is worth leaving on in development builds: without it, a silent game gives you nothing
    /// to look at, and with it you can see whether events are posting and objects are moving. It
    /// has no effect against a Release-configuration native shim, where Wwise compiles comms out.
    /// </summary>
    /// <param name="useSubfoldering">
    /// The on-disk LAYOUT of <paramref name="soundBankPath"/>. Auto-defined SoundBanks — Wwise's
    /// default since 2021 — put banks under <c>Event/</c> and <c>Bus/</c> and loose media under
    /// <c>Media/</c>; a hand-defined bank list is usually flat. Getting it wrong is not a load
    /// failure: banks load either way, and then every voice reports "Media &lt;id&gt; was not
    /// loaded for this source" and the game is silent. Null auto-detects from the directory,
    /// which is right for every layout Wwise itself produces.
    /// </param>
    /// <returns>False when Wwise is unavailable or initialization failed. Not an error the caller
    /// needs to handle beyond running silent.</returns>
    public bool TryInitialize(
        string soundBankPath, bool enableProfiler = true, bool? useSubfoldering = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized)
        {
            return true;
        }

        // Detected from the directory rather than assumed, and the test is NOT "does Media/
        // exist". Media/ exists in every layout that has streamed or loose sources, including the
        // ordinary one. Subfoldering is the separate option that nests media in a further level of
        // directories INSIDE Media/, for projects with too many files for one folder. So the
        // question is whether Media/ holds directories rather than files — which is also why
        // getting it wrong is quiet: banks still load and only streamed voices go missing.
        var subfoldered = useSubfoldering ?? IsSubfoldered(soundBankPath);

        int result;
        try
        {
            result = WwiseNative.Init(soundBankPath, enableProfiler ? 1 : 0, subfoldered ? 1 : 0);
        }
        catch (DllNotFoundException)
        {
            // No Wwise SDK on this machine, so Wwise.targets built nothing. Expected, not
            // exceptional: the game runs silent.
            LastError = 0;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            // A stale libParadiseWwise from before an API change. Worth separating from "absent"
            // because the fix is different: rebuild the shim.
            LastError = 0;
            return false;
        }

        if (result != WwiseNative.Success)
        {
            LastError = result;
            return false;
        }

        _initialized = true;
        return true;
    }

    /// <summary>True when generated media is nested one level deeper inside <c>Media/</c>, which
    /// is Wwise's "use SoundBank subfolders" option rather than its default output.</summary>
    private static bool IsSubfoldered(string soundBankPath)
    {
        var media = Path.Combine(soundBankPath, "Media");
        return Directory.Exists(media)
            && Directory.EnumerateDirectories(media).Any()
            && !Directory.EnumerateFiles(media, "*.wem").Any();
    }

    /// <summary>
    /// Load a soundbank by file name, e.g. <c>"Init.bnk"</c>. Blocking.
    ///
    /// <c>Init.bnk</c> must be loaded FIRST and must always be loaded: it carries the bus layout,
    /// and without it every other bank loads successfully and plays nothing.
    /// </summary>
    public bool LoadBank(string bankName)
    {
        if (!_initialized)
        {
            return false;
        }

        var result = WwiseNative.LoadBank(bankName, out _);
        if (result != WwiseNative.Success)
        {
            LastError = result;
            return false;
        }
        return true;
    }

    public bool UnloadBank(string bankName)
    {
        if (!_initialized)
        {
            return false;
        }

        var result = WwiseNative.UnloadBank(bankName);
        if (result != WwiseNative.Success)
        {
            LastError = result;
            return false;
        }
        return true;
    }

    /// <summary>Process the frame's queued commands. Nothing is heard until this is called, and
    /// it must be called exactly once per frame — skipping it stalls audio, calling it twice
    /// makes the engine's internal timing inconsistent with the game's.</summary>
    public void RenderAudio()
    {
        if (_initialized)
        {
            WwiseNative.RenderAudio();
        }
    }

    // ---- game objects -------------------------------------------------------------------------

    /// <summary>Register an emitter or listener. <paramref name="name"/> is shown in the profiler
    /// and nowhere else, so it should say which entity this is rather than which sound it makes.</summary>
    public bool Register(WwiseGameObject gameObject, string? name = null)
    {
        if (!_initialized)
        {
            return false;
        }

        var result = WwiseNative.RegisterGameObj(gameObject, name);
        if (result != WwiseNative.Success)
        {
            LastError = result;
            return false;
        }
        return true;
    }

    public void Unregister(WwiseGameObject gameObject)
    {
        if (_initialized)
        {
            WwiseNative.UnregisterGameObj(gameObject);
        }
    }

    /// <summary>
    /// Position an object from a world position and a yaw.
    ///
    /// The angle convention is the engine's: 0 faces +Z, increasing toward +X, which is what
    /// <c>atan2(forward.X, forward.Z)</c> produces. Wwise's default floor plane is XZ with +Y up,
    /// the same basis, so positions pass through unchanged rather than needing a handedness flip.
    /// </summary>
    public void SetPosition(WwiseGameObject gameObject, Vector3 position, float headingRadians)
    {
        if (!_initialized)
        {
            return;
        }

        var front = new Vector3(MathF.Sin(headingRadians), 0f, MathF.Cos(headingRadians));
        SetPosition(gameObject, position, front, Vector3.UnitY);
    }

    /// <summary>Position an object with an explicit orientation. The vectors need not be
    /// normalized or orthogonal — the native side repairs them, because Wwise rejects a
    /// malformed orientation outright and a rejected position is silent rather than loud.</summary>
    public void SetPosition(WwiseGameObject gameObject, Vector3 position, Vector3 front, Vector3 top)
    {
        if (!_initialized)
        {
            return;
        }

        WwiseNative.SetPosition(
            gameObject,
            position.X, position.Y, position.Z,
            front.X, front.Y, front.Z,
            top.X, top.Y, top.Z);
    }

    /// <summary>Make <paramref name="gameObject"/> the listener for every object that has not
    /// chosen its own. Must be called after the object is registered, or nothing is audible.</summary>
    public bool SetDefaultListener(WwiseGameObject gameObject)
    {
        if (!_initialized)
        {
            return false;
        }

        var result = WwiseNative.SetDefaultListener(gameObject);
        if (result != WwiseNative.Success)
        {
            LastError = result;
            return false;
        }
        return true;
    }

    // ---- playback -----------------------------------------------------------------------------

    /// <summary>Post an event on an object.</summary>
    /// <returns>A playing id for stopping this instance later, or
    /// <see cref="WwisePlayingId.Invalid"/> if the event did not start — which most often means
    /// the bank carrying it is not loaded.</returns>
    public WwisePlayingId PostEvent(WwiseId eventId, WwiseGameObject gameObject)
    {
        if (!_initialized || !eventId.IsValid)
        {
            return WwisePlayingId.Invalid;
        }

        return new WwisePlayingId(WwiseNative.PostEvent(eventId, gameObject));
    }

    /// <summary>Stop one posted instance. The fade avoids the click that ending a waveform
    /// mid-cycle produces; 0 is honest only for sounds that end at a zero crossing.</summary>
    public void Stop(WwisePlayingId playingId, int fadeOutMs = 100)
    {
        if (_initialized && playingId.IsValid)
        {
            WwiseNative.StopPlayingId(playingId.Value, fadeOutMs);
        }
    }

    /// <summary>Stop everything on one object, or everything everywhere when passed
    /// <see cref="WwiseGameObject.Global"/>.</summary>
    public void StopAll(WwiseGameObject gameObject)
    {
        if (_initialized)
        {
            WwiseNative.StopAll(gameObject);
        }
    }

    // ---- offline capture ------------------------------------------------------------------------

    /// <summary>
    /// Also write the master output to a .wav, until <see cref="StopOutputCapture"/>.
    ///
    /// This is the only way to assert that something is actually AUDIBLE, which is worth having
    /// because almost every way a Wwise integration fails is silent: an unresolved switch, a
    /// missing bank, an unregistered codec, and an event whose container has no children all
    /// return success and produce no sound. A captured file that is all zeroes separates "played
    /// nothing" from "played something" when no return code can.
    ///
    /// The path is resolved by the low-level I/O hook, so it lands under the soundbank directory
    /// unless it is absolute.
    /// </summary>
    public bool StartOutputCapture(string fileName)
    {
        if (!_initialized)
        {
            return false;
        }

        var result = WwiseNative.StartOutputCapture(fileName);
        if (result != WwiseNative.Success)
        {
            LastError = result;
            return false;
        }
        return true;
    }

    public void StopOutputCapture()
    {
        if (_initialized)
        {
            WwiseNative.StopOutputCapture();
        }
    }

    // ---- parameters ---------------------------------------------------------------------------

    /// <summary>Set a game parameter. Defaults to the global scope; pass an object to give that
    /// emitter its own value.</summary>
    public void SetRtpc(WwiseId rtpcId, float value, WwiseGameObject? gameObject = null)
    {
        if (_initialized && rtpcId.IsValid)
        {
            WwiseNative.SetRtpcValue(rtpcId, value, gameObject ?? WwiseGameObject.Global);
        }
    }

    /// <summary>Set a switch on one object — which variant of a sound it plays.</summary>
    public void SetSwitch(WwiseId switchGroup, WwiseId switchState, WwiseGameObject gameObject)
    {
        if (_initialized && switchGroup.IsValid && switchState.IsValid)
        {
            WwiseNative.SetSwitch(switchGroup, switchState, gameObject);
        }
    }

    /// <summary>Set a global state — which mix the whole game is in.</summary>
    public void SetState(WwiseId stateGroup, WwiseId state)
    {
        if (_initialized && stateGroup.IsValid && state.IsValid)
        {
            WwiseNative.SetState(stateGroup, state);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_initialized)
        {
            WwiseNative.Term();
            _initialized = false;
        }
    }
}

/// <summary>
/// One posted instance of an event, as returned by <see cref="WwiseSoundEngine.PostEvent"/>.
///
/// Distinct from <see cref="WwiseId"/> because the two mean opposite things: an event id names
/// a sound that can be played any number of times, a playing id names one playback that is
/// happening now. Stopping takes the second; posting takes the first.
/// </summary>
public readonly record struct WwisePlayingId(uint Value)
{
    public static WwisePlayingId Invalid => new(WwiseNative.InvalidId);

    public bool IsValid => Value != WwiseNative.InvalidId;
}
