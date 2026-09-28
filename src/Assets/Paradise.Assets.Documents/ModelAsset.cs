using Tomlyn.Model;

namespace Paradise.Assets.Documents;

/// <summary>One model of a source holding several — an asset collection of a <c>.blend</c> — named by the GUID the collection carries and, as a hint, its name.</summary>
/// <remarks>
/// The GUID is the identity: a renamed collection keeps it, so every document naming the model
/// keeps naming it. The name is what the collection was called when the document was last
/// written, repaired like a reference's path half, and what a message shows an author. A document
/// writes it as <c>asset = { guid = "…", name = "…" }</c>, which the Blender addon reads too.
/// </remarks>
public sealed record ModelAsset(Guid Guid, string Name)
{
    public const string GuidKey = "guid";

    public const string NameKey = "name";

    /// <summary>The model as a message names it: <c>'Lamp_Tall' (guid …)</c>.</summary>
    public override string ToString() => $"'{Name}' (guid {DocumentGuid.Format(Guid)})";

    public CanonicalInlineTable Write() => new()
    {
        { GuidKey, DocumentGuid.Format(Guid) },
        { NameKey, Name },
    };

    /// <exception cref="Exception">Through <paramref name="fail"/>: the table is not exactly a non-empty UUID <c>guid</c> and a non-empty <c>name</c>.</exception>
    internal static ModelAsset Read(TomlTable table, Func<string, Exception> fail)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(fail);

        TomlDocumentReader.RejectUnknownKeys(table, "in 'asset'", [GuidKey, NameKey], fail);
        var guidText = TomlDocumentReader.OptionalString(table, GuidKey, "in 'asset'", fail);
        if (!DocumentGuid.TryParse(guidText, out var guid) || guid == Guid.Empty)
        {
            throw fail("'asset' must name its asset collection as { guid, name } with a non-empty UUID guid");
        }

        var name = TomlDocumentReader.OptionalString(table, NameKey, "in 'asset'", fail);
        if (string.IsNullOrEmpty(name)) throw fail("'asset' must name its asset collection as { guid, name } with a non-empty name");
        return new ModelAsset(guid, name);
    }
}
