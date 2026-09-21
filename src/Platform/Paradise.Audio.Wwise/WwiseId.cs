using System.Globalization;
using Paradise.Audio.Wwise.Interop;

namespace Paradise.Audio.Wwise;

/// <summary>A Wwise event, RTPC, switch or state identifier.</summary>
/// <remarks>
/// Prefer generated <c>Wwise_IDs.h</c> constants in shipping code.
/// <see cref="FromName"/> calls Wwise's lowercase-name hash for runtime lookup.
/// </remarks>
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
