using System.Globalization;
using Paradise.Audio.Wwise.Interop;

namespace Paradise.Audio.Wwise;

/// <summary>
/// A registered Wwise game object: anything that emits or hears sound.
///
/// This is a strongly-typed id rather than a handle with behaviour. Wwise object ids and event
/// ids are both plain integers, and every posting call takes one of each — so the single most
/// likely mistake in an audio integration is passing them in the wrong order, which produces no
/// error and no sound. Separate types make that a compile error.
///
/// Ids are the caller's to allocate. <see cref="FromIndex"/> exists so a host can derive stable
/// ids from something it already has (an actor's index, an emitter's slot) instead of keeping a
/// parallel counter, which would drift the moment the scene reloads.
/// </summary>
public readonly record struct WwiseGameObject(ulong Id)
{
    /// <summary>The global scope: an RTPC set here applies to every object that has no value of
    /// its own. Not a real object — it cannot be registered or positioned.</summary>
    public static WwiseGameObject Global => new(WwiseNative.GlobalObject);

    /// <summary>
    /// A stable id for the <paramref name="index"/>th object of a given <paramref name="category"/>.
    ///
    /// The category occupies the high bits so two different kinds of thing (actors and authored
    /// emitters, say) can both count from zero without colliding — which they otherwise would,
    /// silently, with the second registration failing and one of them going mute.
    /// </summary>
    public static WwiseGameObject FromIndex(byte category, int index) =>
        new(((ulong)category << 32) | (uint)index);

    public static implicit operator ulong(WwiseGameObject gameObject) => gameObject.Id;

    public override string ToString() => Id.ToString(CultureInfo.InvariantCulture);
}
