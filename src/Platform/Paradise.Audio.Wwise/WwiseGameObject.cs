using System.Globalization;
using Paradise.Audio.Wwise.Interop;

namespace Paradise.Audio.Wwise;

/// <summary>A typed ID for a registered Wwise emitter or listener.</summary>
/// <remarks>Distinct from event IDs to prevent argument swaps; callers allocate IDs or derive them with <see cref="FromIndex"/>.</remarks>
public readonly record struct WwiseGameObject(ulong Id)
{
    /// <summary>The global scope: an RTPC set here applies to every object that has no value of
    /// its own. Not a real object — it cannot be registered or positioned.</summary>
    public static WwiseGameObject Global => new(WwiseNative.GlobalObject);

    /// <summary>
    /// A stable id for the <paramref name="index"/>th object of a given <paramref name="category"/>.
    ///
    /// The category occupies bits 32..39 and the index occupies bits 0..31, keeping categories
    /// disjoint when each allocates its own indices.
    /// </summary>
    public static WwiseGameObject FromIndex(byte category, int index) =>
        new(((ulong)category << 32) | (uint)index);

    public static implicit operator ulong(WwiseGameObject gameObject) => gameObject.Id;

    public override string ToString() => Id.ToString(CultureInfo.InvariantCulture);
}
