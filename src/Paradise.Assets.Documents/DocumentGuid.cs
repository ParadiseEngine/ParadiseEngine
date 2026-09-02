using System.Globalization;

namespace Paradise.Assets.Documents;

/// <summary>Canonical form is hyphenated lowercase, what Python's <c>str(uuid)</c> and .NET's <c>Guid.ToString()</c> both produce; parsing also accepts the undashed form the Godot host stored, so migrated scenes keep their identities.</summary>
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
