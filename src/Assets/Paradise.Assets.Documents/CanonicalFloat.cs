using System.Globalization;

namespace Paradise.Assets.Documents;

/// <summary>Widens float32 using its shortest round-trip decimal spelling.</summary>
/// <remarks>Matches the Blender writer: <c>0.1f</c> becomes <c>0.1</c>, not <c>0.10000000149011612</c>.</remarks>
internal static class CanonicalFloat
{
    public static double Widen(float value)
    {
        if (!float.IsFinite(value)) return value;
        return double.Parse(value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}
