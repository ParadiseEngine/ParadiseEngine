using System.Globalization;

namespace Paradise.Assets.Documents;

/// <summary>
/// The GUID conventions of authored documents.
/// </summary>
/// <remarks>
/// Canonical form is hyphenated lowercase (<c>8-4-4-4-12</c>) — what Python's
/// <c>str(uuid.uuid4())</c> and .NET's <c>Guid.ToString()</c> both produce, and the form the
/// Blender addon already mints for <c>entity_guid</c>. Parsing additionally accepts the 32-digit
/// undashed form, mirroring the addon's <c>parse_guid</c>, because the Godot host stored ids
/// that way and migrated scenes keep their identities.
/// </remarks>
public static class DocumentGuid
{
    /// <summary>Formats <paramref name="guid"/> canonically: hyphenated lowercase.</summary>
    public static string Format(Guid guid) => guid.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses the canonical hyphenated form or the undashed 32-digit form, case-insensitively.
    /// Braced, parenthesized and other exotic .NET forms are rejected — no tool writes them, so
    /// accepting them would just widen what the Python mirror has to match.
    /// </summary>
    /// <param name="text">The candidate GUID text.</param>
    /// <param name="guid">The parsed value.</param>
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
