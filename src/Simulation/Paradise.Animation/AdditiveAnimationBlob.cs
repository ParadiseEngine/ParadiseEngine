namespace Paradise.Animation;

/// <summary>Stores a clip of deltas from a reference pose, the only kind of clip an additive layer plays.</summary>
/// <remarks>Its own type keeps an absolute clip from being applied as a delta: build one with
/// <see cref="Offline.AdditiveAnimationBuilder"/>. The deltas sample like any clip, by ref through <see cref="Deltas"/>:
/// translation offsets, rotations relative to the reference rotation (<c>reference × delta = source</c>) and scale
/// factors (<c>reference × delta = source</c> per axis).</remarks>
public struct AdditiveAnimationBlob
{
    public AnimationBlob Deltas;
}
