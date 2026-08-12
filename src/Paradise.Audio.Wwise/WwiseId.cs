using System.Globalization;
using Paradise.Audio.Wwise.Interop;

namespace Paradise.Audio.Wwise;

/// <summary>
/// A Wwise object identifier — an event, RTPC, switch group, switch, state group, or state.
///
/// Wwise names everything by a 32-bit FNV hash of its lowercased name, and the authoring tool
/// writes those numbers into <c>Wwise_IDs.h</c> at bank-generation time. There are therefore two
/// honest ways to name a sound from game code, and this type supports both:
///
///   - <see cref="FromName"/>, which hashes at runtime. Convenient, and correct by construction
///     because it calls the sound engine's own hash rather than reimplementing it.
///   - the generated numeric constants, passed to the constructor. No string work per call, and a
///     renamed event becomes a compile error instead of a sound that silently stops playing.
///
/// Prefer the generated constants in shipping paths. <see cref="FromName"/> earns its place while
/// bringing a feature up, before the generated header is wired in.
/// </summary>
public readonly record struct WwiseId(uint Value)
{
    /// <summary>The id Wwise assigns to nothing — what a failed lookup returns.</summary>
    public static WwiseId Invalid => new(WwiseNative.InvalidId);

    public bool IsValid => Value != WwiseNative.InvalidId;

    /// <summary>
    /// Hash a name the way the authoring tool does, by asking the sound engine to do it.
    ///
    /// This does NOT check that anything by that name exists — the hash of a typo is a perfectly
    /// valid number that simply matches nothing, and posting it fails silently at the Wwise end.
    /// That is Wwise's model, not a gap here: names only exist in the authoring project.
    /// </summary>
    public static WwiseId FromName(string name) =>
        string.IsNullOrEmpty(name) ? Invalid : new WwiseId(WwiseNative.GetIdFromString(name));

    public static implicit operator uint(WwiseId id) => id.Value;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
