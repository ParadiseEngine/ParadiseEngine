using System.Globalization;
using System.Numerics;

namespace Paradise.Audio;

/// <summary>An emitter or listener the game registers with an <see cref="IAudioEngine"/>.</summary>
/// <remarks>The game allocates ids; <see cref="FromIndex"/> keeps categories of objects disjoint.</remarks>
public readonly record struct AudioEmitter(ulong Id)
{
    /// <summary>The global scope: a parameter set here applies to every emitter without a value of its own,
    /// and <see cref="IAudioEngine.StopAll"/> on it stops everything. It cannot be registered or positioned.</summary>
    public static AudioEmitter Global => new(ulong.MaxValue);

    /// <summary>The <paramref name="index"/>th emitter of <paramref name="category"/>: the category occupies
    /// bits 32..39 and the index bits 0..31.</summary>
    public static AudioEmitter FromIndex(byte category, int index) => new(((ulong)category << 32) | (uint)index);

    public override string ToString() => Id.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A backend's identifier for a named event, parameter, switch or state.</summary>
/// <remarks>Resolve names once with <see cref="IAudioEngine.Resolve"/> and keep the result: posting by
/// resolved name allocates nothing and needs no lookup per call.</remarks>
public readonly record struct AudioName(uint Value)
{
    public static AudioName Invalid => default;

    public bool IsValid => Value != 0;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One playing instance of an event, as returned by <see cref="IAudioEngine.Post"/>.</summary>
/// <remarks>Distinct from <see cref="AudioName"/>: an event name can be posted any number of times; a
/// playback names one instance that is sounding now.</remarks>
public readonly record struct AudioPlayback(uint Value)
{
    public static AudioPlayback Invalid => default;

    public bool IsValid => Value != 0;
}

/// <summary>Event-based game audio, the model shared by Wwise, FMOD and similar middleware.</summary>
/// <remarks>
/// Audio is optional: when <see cref="IsAvailable"/> is false every operation is accepted and does nothing,
/// so a host without audio (a server, the browser today, a test) runs the same game code. Positions use
/// the engine's convention: right-handed, Y-up, -Z forward, in metres. Call every member from one thread,
/// normally the simulation's, so events and positions reach the backend in order.
/// </remarks>
public interface IAudioEngine : IDisposable
{
    /// <summary>Whether calls reach a sound engine.</summary>
    bool IsAvailable { get; }

    /// <summary>The backend's identifier for <paramref name="name"/>, or <see cref="AudioName.Invalid"/> for an
    /// empty name. Does not check that anything by that name exists.</summary>
    AudioName Resolve(string name);

    /// <summary>Loads a content bank by name. Blocking.</summary>
    bool LoadBank(string bank);

    bool UnloadBank(string bank);

    /// <summary>Registers an emitter or listener; <paramref name="debugName"/> appears only in profiling tools.</summary>
    bool Register(AudioEmitter emitter, string? debugName = null);

    void Unregister(AudioEmitter emitter);

    /// <summary>Places a registered emitter or listener. <paramref name="forward"/> and <paramref name="up"/>
    /// need not be normalised.</summary>
    void SetTransform(AudioEmitter emitter, Vector3 position, Vector3 forward, Vector3 up);

    /// <summary>Makes a registered object the listener for emitters without an explicit one.</summary>
    bool SetDefaultListener(AudioEmitter listener);

    /// <summary>Starts an event on an emitter; <see cref="AudioPlayback.Invalid"/> when it did not start,
    /// most often because its bank is not loaded.</summary>
    AudioPlayback Post(AudioName audioEvent, AudioEmitter emitter);

    /// <summary>Stops one playback, fading out over <paramref name="fadeOut"/> to avoid clicks.</summary>
    void Stop(AudioPlayback playback, TimeSpan fadeOut);

    /// <summary>Stops everything on one emitter, or everywhere for <see cref="AudioEmitter.Global"/>.</summary>
    void StopAll(AudioEmitter emitter);

    /// <summary>Sets a continuous parameter globally, or for one emitter.</summary>
    void SetParameter(AudioName parameter, float value, AudioEmitter? emitter = null);

    /// <summary>Selects which variant of its sounds one emitter plays.</summary>
    void SetSwitch(AudioName group, AudioName value, AudioEmitter emitter);

    /// <summary>Selects a global mix state.</summary>
    void SetState(AudioName group, AudioName value);

    /// <summary>Submits queued events, positions and parameters; call once per frame.</summary>
    void Update();
}
