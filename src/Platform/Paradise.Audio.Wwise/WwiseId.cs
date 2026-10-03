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
    /// <summary>The zero sentinel for an invalid Wwise identifier.</summary>
    public static WwiseId Invalid => new(WwiseNative.InvalidId);

    public bool IsValid => Value != WwiseNative.InvalidId;

    /// <summary>
    /// Hash a name the way the authoring tool does, by asking the sound engine to do it.
    ///
    /// This does NOT check that anything by that name exists — the hash of a typo is a perfectly
    /// nonzero number. Event existence is resolved when the event is posted.
    /// </summary>
    public static WwiseId FromName(string name) =>
        string.IsNullOrEmpty(name) ? Invalid : new WwiseId(WwiseNative.GetIdFromString(name));

    public static implicit operator uint(WwiseId id) => id.Value;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
