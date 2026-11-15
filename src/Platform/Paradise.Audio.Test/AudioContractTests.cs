using System.Numerics;
using Paradise.Audio.Wwise;

namespace Paradise.Audio.Test;

public class AudioContractTests
{
    [Test]
    public async Task the_null_engine_accepts_everything_and_plays_nothing()
    {
        IAudioEngine audio = NullAudioEngine.Instance;
        var emitter = AudioEmitter.FromIndex(category: 1, index: 7);
        var footstep = audio.Resolve("Play_Footstep");

        await Assert.That(audio.IsAvailable).IsFalse();
        await Assert.That(audio.LoadBank("Init.bnk")).IsFalse();
        await Assert.That(audio.Register(emitter, "player")).IsFalse();
        audio.SetTransform(emitter, Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
        await Assert.That(audio.Post(footstep, emitter).IsValid).IsFalse();
        audio.SetParameter(audio.Resolve("Speed"), 3f, emitter);
        audio.StopAll(AudioEmitter.Global);
        audio.Update();
    }

    [Test]
    public async Task null_engine_names_are_stable_case_insensitive_and_never_invalid()
    {
        var audio = NullAudioEngine.Instance;

        await Assert.That(audio.Resolve("Play_Footstep")).IsEqualTo(audio.Resolve("play_footstep"));
        await Assert.That(audio.Resolve("Play_Footstep")).IsNotEqualTo(audio.Resolve("Play_Jump"));
        await Assert.That(audio.Resolve("Play_Footstep").IsValid).IsTrue();
        await Assert.That(audio.Resolve("").IsValid).IsFalse();
    }

    [Test]
    public async Task emitter_categories_do_not_collide()
    {
        await Assert.That(AudioEmitter.FromIndex(1, 0)).IsNotEqualTo(AudioEmitter.FromIndex(2, 0));
        await Assert.That(AudioEmitter.FromIndex(1, 5).Id).IsEqualTo((1UL << 32) | 5UL);
    }

    /// <summary>Wwise behind the contract degrades like the null engine until it initializes, so game code
    /// written against <see cref="IAudioEngine"/> runs without a licensed SDK.</summary>
    [Test]
    public async Task an_uninitialized_wwise_engine_behaves_as_unavailable_audio()
    {
        using var wwise = new WwiseSoundEngine();
        IAudioEngine audio = wwise;
        var emitter = AudioEmitter.FromIndex(1, 1);

        await Assert.That(audio.IsAvailable).IsFalse();
        await Assert.That(audio.Resolve("Play_Footstep").IsValid).IsFalse();
        await Assert.That(audio.Register(emitter)).IsFalse();
        await Assert.That(audio.Post(new AudioName(1234), emitter).IsValid).IsFalse();
        audio.SetTransform(emitter, Vector3.One, -Vector3.UnitZ, Vector3.UnitY);
        audio.Stop(new AudioPlayback(5), TimeSpan.FromMilliseconds(50));
        audio.SetParameter(new AudioName(1), 1f);
        audio.SetSwitch(new AudioName(1), new AudioName(2), emitter);
        audio.SetState(new AudioName(1), new AudioName(2));
        audio.Update();
    }

    [Test]
    public async Task the_global_emitter_is_wwises_global_object()
    {
        await Assert.That(AudioEmitter.Global.Id).IsEqualTo(WwiseGameObject.Global.Id);
    }
}
