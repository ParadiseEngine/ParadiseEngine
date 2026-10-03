using System.Numerics;
using Paradise.Audio.Wwise.Interop;

namespace Paradise.Audio.Wwise;

/// <summary>Owns the Wwise engine lifetime and managed audio operations.</summary>
/// <remarks>
/// Audio is optional: failed initialization returns false, records <see cref="LastError"/>, and
/// audio operations do nothing until initialization succeeds. Native-load failures use error code 0.
/// Use one caller thread, normally simulation, for the entire lifetime to preserve event/position ordering.
/// </remarks>
public sealed class WwiseSoundEngine : IDisposable
{
    private bool _initialized;
    private bool _disposed;

    /// <summary>The last native error code recorded by a bool-returning operation, or 0 when no code is stored.</summary>
    /// <remarks>Success does not clear this value; native-load failures set it to 0. Void operations and PostEvent do not update it.</remarks>
    public int LastError { get; private set; }

    /// <summary>True when the sound engine is up and calls will actually reach it.</summary>
    public bool IsInitialized => _initialized;

    /// <summary>Initializes Wwise and resolves soundbanks against <paramref name="soundBankPath"/>.</summary>
    /// <param name="soundBankPath">A host filesystem path used by Wwise's native I/O hook, not a Zio mount path.</param>
    /// <param name="enableProfiler">Enables profiler communication in Debug/Profile native shims; Release ignores it.</param>
    /// <param name="useSubfoldering">Whether generated media uses subdirectories beneath <c>Media/</c>.
    /// Null infers this from subdirectories with no direct <c>*.wem</c> files; pass a value for layouts
    /// that this heuristic cannot identify.</param>
    /// <returns>False when the native shim is unavailable or Wwise initialization fails.</returns>
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
            // A missing shim or one of its dependencies leaves audio unavailable.
            LastError = 0;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            // A shim with an incompatible export set must be rebuilt against these bindings.
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

    /// <summary>Infers media subfoldering from directories beneath <c>Media/</c> and no direct <c>*.wem</c> files.</summary>
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
    /// Load <c>Init.bnk</c> before content banks to establish the sound engine's project settings and bus layout.
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

    /// <summary>Submits queued events, positions and parameter changes to the audio engine.</summary>
    /// <remarks>Call regularly, usually once per game frame; existing audio can continue between calls.</remarks>
    public void RenderAudio()
    {
        if (_initialized)
        {
            WwiseNative.RenderAudio();
        }
    }

    // game objects

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
    /// This overload uses 0 toward +Z and increasing angles toward +X, matching
    /// <c>atan2(forward.X, forward.Z)</c>. This differs from the engine's -Z-forward convention;
    /// pass explicit orientation vectors when starting from an engine transform.
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

    /// <summary>Positions an object with explicit front and top vectors.</summary>
    /// <remarks>The native shim normalizes orientation and repairs near-zero or parallel directions.
    /// Non-finite values and overflow during normalization are not checked.</remarks>
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

    /// <summary>Sets a registered object as the default listener for emitters without explicit listeners.</summary>
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

    // playback

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

    /// <summary>Stops one posted instance with an optional fade in milliseconds.</summary>
    /// <remarks>A fade can reduce clicks from abrupt waveform truncation.</remarks>
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

    // offline capture

    /// <summary>
    /// Also write the master output to a .wav, until <see cref="StopOutputCapture"/>.
    ///
    /// Capture verifies the produced signal when successful API calls alone do not prove that
    /// the authored event produced audible output.
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

    // parameters

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
