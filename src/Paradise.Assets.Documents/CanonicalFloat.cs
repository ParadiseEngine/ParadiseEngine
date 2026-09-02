using System.Globalization;

namespace Paradise.Assets.Documents;

/// <summary>The one float32 → float64 widening the document model uses.</summary>
/// <remarks>
/// Shortest decimal that round-trips the float32, not the bit-exact double: <c>0.1f</c> is
/// written <c>0.1</c>, never <c>0.10000000149011612</c>. Every writer path (table, inline table,
/// transform codec) goes through here so a value authored as float32 has one spelling on the
/// wire; three different widenings once put 17-digit noise into every transform diff (issue #200).
/// The Blender addon widens Blender's float32 channels the same way.
/// </remarks>
internal static class CanonicalFloat
{
    public static double Widen(float value)
    {
        if (!float.IsFinite(value)) return value;
        return double.Parse(value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}
