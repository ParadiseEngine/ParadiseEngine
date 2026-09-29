namespace Paradise.Animation;

/// <summary>How a layer combines its blended playbacks with the pose beneath it.</summary>
public enum AnimationLayerMode
{
    /// <summary>Replaces the pose beneath by the layer's weight, scaled per joint by its mask: 0 keeps the pose beneath, 1 takes the layer's.</summary>
    Override,

    /// <summary>Applies <see cref="AdditiveAnimationBlob"/> deltas to the pose beneath, scaled by the layer's weight and mask.</summary>
    Additive,
}

/// <summary>Identifies one clip playing in an <see cref="AnimationPlayer"/>.</summary>
/// <remarks>Valid until the playback is removed — explicitly, or when a fade that removes it ends — and never
/// identifies a later playback that reuses its storage. <c>default</c> identifies nothing.</remarks>
public readonly struct PlaybackHandle : IEquatable<PlaybackHandle>
{
    internal PlaybackHandle(int player, int slot, int generation)
    {
        Player = player;
        Slot = slot;
        Generation = generation;
    }

    internal int Player { get; }

    internal int Slot { get; }

    internal int Generation { get; }

    public bool Equals(PlaybackHandle other) => Player == other.Player && Slot == other.Slot && Generation == other.Generation;

    public override bool Equals(object? obj) => obj is PlaybackHandle other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Player, Slot, Generation);

    public override string ToString() => Player == 0 ? "Playback (none)" : $"Playback {Slot}.{Generation}";

    public static bool operator ==(PlaybackHandle left, PlaybackHandle right) => left.Equals(right);

    public static bool operator !=(PlaybackHandle left, PlaybackHandle right) => !left.Equals(right);
}

/// <summary>Identifies one layer of an <see cref="AnimationPlayer"/>.</summary>
/// <remarks>Valid until the layer is removed; <see cref="AnimationPlayer.BaseLayer"/> never is. <c>default</c> identifies nothing.</remarks>
public readonly struct LayerHandle : IEquatable<LayerHandle>
{
    internal LayerHandle(int player, int slot, int generation)
    {
        Player = player;
        Slot = slot;
        Generation = generation;
    }

    internal int Player { get; }

    internal int Slot { get; }

    internal int Generation { get; }

    public bool Equals(LayerHandle other) => Player == other.Player && Slot == other.Slot && Generation == other.Generation;

    public override bool Equals(object? obj) => obj is LayerHandle other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Player, Slot, Generation);

    public override string ToString() => Player == 0 ? "Layer (none)" : $"Layer {Slot}.{Generation}";

    public static bool operator ==(LayerHandle left, LayerHandle right) => left.Equals(right);

    public static bool operator !=(LayerHandle left, LayerHandle right) => !left.Equals(right);
}

/// <summary>Identifies one sync group of an <see cref="AnimationPlayer"/>.</summary>
/// <remarks>Valid until the group is removed. <c>default</c> identifies nothing.</remarks>
public readonly struct SyncGroupHandle : IEquatable<SyncGroupHandle>
{
    internal SyncGroupHandle(int player, int slot, int generation)
    {
        Player = player;
        Slot = slot;
        Generation = generation;
    }

    internal int Player { get; }

    internal int Slot { get; }

    internal int Generation { get; }

    public bool Equals(SyncGroupHandle other) => Player == other.Player && Slot == other.Slot && Generation == other.Generation;

    public override bool Equals(object? obj) => obj is SyncGroupHandle other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Player, Slot, Generation);

    public override string ToString() => Player == 0 ? "Sync group (none)" : $"Sync group {Slot}.{Generation}";

    public static bool operator ==(SyncGroupHandle left, SyncGroupHandle right) => left.Equals(right);

    public static bool operator !=(SyncGroupHandle left, SyncGroupHandle right) => !left.Equals(right);
}

/// <summary>A playback's state as <see cref="AnimationPlayer.GetState(PlaybackHandle)"/> read it.</summary>
/// <param name="Layer">The layer it plays in.</param>
/// <param name="SyncGroup">The group driving its clock, or <c>default</c> when it keeps its own.</param>
/// <param name="Weight">Its weight relative to the other playbacks of its layer.</param>
/// <param name="TargetWeight">Where the running fade ends; <paramref name="Weight"/> when none runs.</param>
/// <param name="IsFading">Whether a weight fade runs.</param>
/// <param name="IsFadingOut">Whether the playback is removed when its fade reaches zero.</param>
/// <param name="Time">Seconds into the clip.</param>
/// <param name="Duration">The clip's length in seconds.</param>
/// <param name="Rate">Its own rate; a sync group's rate drives it instead while it is a member.</param>
/// <param name="IsLooping">Whether time wraps rather than clamps.</param>
/// <param name="IsPaused">Whether its clock is stopped.</param>
/// <param name="IsFinished">A one-shot at its end, or at its start when playing backwards; it holds that pose until removed.</param>
public readonly record struct PlaybackState(
    LayerHandle Layer,
    SyncGroupHandle SyncGroup,
    float Weight,
    float TargetWeight,
    bool IsFading,
    bool IsFadingOut,
    float Time,
    float Duration,
    float Rate,
    bool IsLooping,
    bool IsPaused,
    bool IsFinished)
{
    /// <summary><see cref="Time"/> as a fraction of <see cref="Duration"/>.</summary>
    public float NormalizedTime => Duration > 0f ? Time / Duration : 0f;
}

/// <summary>A layer's state as <see cref="AnimationPlayer.GetState(LayerHandle)"/> read it.</summary>
/// <param name="Mode">How it combines with the pose beneath.</param>
/// <param name="Weight">The weight set for it, in 0..1.</param>
/// <param name="TargetWeight">Where the running weight fade ends; <paramref name="Weight"/> when none runs.</param>
/// <param name="IsFading">Whether its weight, or a <c>Play</c> or <c>Stop</c> fade, is changing.</param>
/// <param name="EffectiveWeight">The weight applied: <paramref name="Weight"/> scaled by any running <c>Play</c> or <c>Stop</c> fade.</param>
/// <param name="IsStopping">Whether a <c>Stop</c> fade runs, which removes the layer's playbacks when it ends.</param>
/// <param name="Mask">The per-joint weights, or null for every joint at full weight.</param>
public readonly record struct LayerState(
    AnimationLayerMode Mode,
    float Weight,
    float TargetWeight,
    bool IsFading,
    float EffectiveWeight,
    bool IsStopping,
    JointMask? Mask);

/// <summary>A sync group's state as <see cref="AnimationPlayer.GetState(SyncGroupHandle)"/> read it.</summary>
/// <param name="Phase">The shared position in the cycle, in 0..1.</param>
/// <param name="Rate">Cycles advance at this rate; negative plays backwards.</param>
/// <param name="MemberCount">Playbacks it drives.</param>
public readonly record struct SyncGroupState(float Phase, float Rate, int MemberCount);
