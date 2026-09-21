using System.Globalization;

namespace Paradise.Assets.Documents;

/// <summary>Formats GUIDs as hyphenated lowercase; also reads legacy undashed Godot GUIDs.</summary>
public static class DocumentGuid
{
    public static string Format(Guid guid) => guid.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>Braced and other exotic .NET forms are rejected: accepting them would widen what the Python mirror has to match.</summary>
    public static bool TryParse(string? text, out Guid guid)
    {
        guid = default;
        if (text is null) return false;
        return text.Length switch
        {
            36 => Guid.TryParseExact(text, "D", out guid),
            32 => Guid.TryParseExact(text, "N", out guid),
            _ => false,
        };
    }
}
