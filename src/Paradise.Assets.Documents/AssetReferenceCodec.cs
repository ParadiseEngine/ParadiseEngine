using Paradise.Authoring;

namespace Paradise.Assets.Documents;

/// <summary>
/// Reading and writing an <see cref="AssetReference"/> as a document value.
/// </summary>
/// <remarks>
/// The wire form is an inline table, <c>{ guid = "…", path = "…" }</c> in that fixed order
/// (guid is the authoritative half, and the Python mirror must produce the same bytes). An
/// absent reference is <c>{}</c> rather than an omitted element, because references sit in
/// arrays where position is meaning — dropping a null slot would shift every entry after it
/// onto the wrong GLB primitive.
/// </remarks>
public static class AssetReferenceCodec
{
    /// <summary>The identity key. First, because it is what resolution uses.</summary>
    public const string GuidKey = "guid";

    /// <summary>The authoring-path key.</summary>
    public const string PathKey = "path";

    /// <summary>
    /// Whether a parsed table was written inline — which in this format means: whether it is an
    /// asset reference, the one thing written inline at value position.
    /// </summary>
    /// <remarks>
    /// Decided from CONTENT, not the parse: the Python mirror's <c>tomllib</c> cannot see the
    /// source form, and both readers must rebuild the same model from the same bytes (the full
    /// argument is issue #187). Exact by definition — <b>empty, or exactly the two string keys
    /// <c>guid</c> and <c>path</c></b> — so that shape is reserved: a payload table matching it
    /// IS a reference, and the empty table <c>{}</c> is a reference to nothing, which is what
    /// makes the null array slot work.
    /// </remarks>
    public static bool IsWrittenInline(IReadOnlyCollection<KeyValuePair<string, object>> table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Count == 0) return true;
        if (table.Count != 2) return false;

        var hasGuid = false;
        var hasPath = false;
        foreach (var (key, value) in table)
        {
            if (value is not string) return false;
            if (string.Equals(key, GuidKey, StringComparison.Ordinal)) hasGuid = true;
            else if (string.Equals(key, PathKey, StringComparison.Ordinal)) hasPath = true;
            else return false;
        }

        return hasGuid && hasPath;
    }

    /// <summary>Renders a reference, or <c>{}</c> for <see langword="null"/>.</summary>
    public static CanonicalInlineTable Write(AssetReference? reference)
    {
        var table = new CanonicalInlineTable();
        if (reference is null || reference.IsEmpty) return table;

        table.Add(GuidKey, DocumentGuid.Format(reference.Guid));
        table.Add(PathKey, reference.Path);
        return table;
    }

    /// <summary>
    /// Reads a reference from a document value.
    /// </summary>
    /// <param name="value">The value under the field, as the reader produced it.</param>
    /// <param name="context">Where this sits, for the error message.</param>
    /// <param name="fail">Builds the document's own exception type.</param>
    /// <returns>The reference, or <see langword="null"/> when the value is <c>{}</c>.</returns>
    public static AssetReference? Read(object? value, string context, Func<string, Exception> fail)
    {
        if (value is not CanonicalInlineTable table)
        {
            throw fail($"holds {Describe(value)} where an asset reference {{ guid, path }} was expected {context}");
        }

        if (table.Count == 0) return null;

        var guidText = table.Value(GuidKey) as string;
        var path = table.Value(PathKey) as string;

        // Both keys are required whenever the table is non-empty. A reference carrying only a
        // path would resolve today and break on the first rename -- which is the failure the guid
        // exists to prevent, so accepting it would quietly give up the guarantee.
        if (guidText is null || path is null)
        {
            throw fail($"has an asset reference missing '{GuidKey}' or '{PathKey}' {context}");
        }

        if (!DocumentGuid.TryParse(guidText, out var guid) || guid == Guid.Empty)
        {
            throw fail($"holds '{guidText}' where an asset reference's '{GuidKey}' must be a non-empty UUID {context}");
        }

        if (path.Length == 0)
        {
            throw fail($"has an asset reference with an empty '{PathKey}' {context}");
        }

        return new AssetReference(guid, path);
    }

    private static string Describe(object? value) => value switch
    {
        null => "nothing",
        string => "a string",
        CanonicalTomlTable => "a table",
        System.Collections.IEnumerable => "an array",
        _ => $"a {value.GetType().Name}",
    };
}
