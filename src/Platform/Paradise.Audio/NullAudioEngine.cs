using System.Numerics;

namespace Paradise.Audio;

/// <summary>An <see cref="IAudioEngine"/> that plays nothing, for hosts without audio.</summary>
/// <remarks>Names still resolve to stable non-zero identifiers (FNV-1a of the lowercase name), so game
/// code that caches them behaves the same with or without a backend.</remarks>
public sealed class NullAudioEngine : IAudioEngine
{
    public static NullAudioEngine Instance { get; } = new();

    public bool IsAvailable => false;

    public AudioName Resolve(string name)
    {
        if (string.IsNullOrEmpty(name)) return AudioName.Invalid;
        var hash = 2166136261u;
        foreach (var character in name)
        {
            hash = (hash ^ char.ToLowerInvariant(character)) * 16777619u;
        }

        return new AudioName(hash == 0 ? 1u : hash);
    }

    public bool LoadBank(string bank) => false;

    public bool UnloadBank(string bank) => false;

    public bool Register(AudioEmitter emitter, string? debugName = null) => false;

    public void Unregister(AudioEmitter emitter)
    {
    }

    public void SetTransform(AudioEmitter emitter, Vector3 position, Vector3 forward, Vector3 up)
    {
    }

    public bool SetDefaultListener(AudioEmitter listener) => false;

    public AudioPlayback Post(AudioName audioEvent, AudioEmitter emitter) => AudioPlayback.Invalid;

    public void Stop(AudioPlayback playback, TimeSpan fadeOut)
    {
    }

    public void StopAll(AudioEmitter emitter)
    {
    }

    public void SetParameter(AudioName parameter, float value, AudioEmitter? emitter = null)
    {
    }

    public void SetSwitch(AudioName group, AudioName value, AudioEmitter emitter)
    {
    }

    public void SetState(AudioName group, AudioName value)
    {
    }

    public void Update()
    {
    }

    public void Dispose()
    {
    }
}
