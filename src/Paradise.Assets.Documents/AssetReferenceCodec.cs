using Paradise.Authoring;

namespace Paradise.Assets.Documents;

/// <summary>
/// Reading and writing an <see cref="AssetReference"/> as a document value.
/// </summary>
/// <remarks>
/// <para>
/// The wire form is an inline table, <c>{ guid = "…", path = "…" }</c>, and an absent reference is
/// the empty one, <c>{}</c>. Key order is fixed at <c>guid</c> then <c>path</c> — guid first
/// because it is the authoritative half, and fixed because the canonical writer emits model order
/// and the Python mirror has to produce the same bytes.
/// </para>
/// <para>
/// <c>{}</c> rather than an omitted element, because a reference most often appears inside an
/// array where position is meaning: <c>MaterialsComponentData.Slots</c> is one entry per GLB
/// primitive, and "dropping a null shifts every override after it onto the wrong primitive, which
/// renders, and is wrong".
/// </para>
/// </remarks>
public static class AssetReferenceCodec
{
    /// <summary>The identity key. First, because it is what resolution uses.</summary>
    public const string GuidKey = "guid";

    /// <summary>The authoring-path key.</summary>
    public const string PathKey = "path";

    /// <summary>
    /// Whether a parsed table is an asset reference, and therefore was written inline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Writing is by type; reading needs this.</b> TOML gives <c>Mesh = { guid = "…" }</c> and
    /// <c>[Mesh]</c> the SAME parse — the form is not recoverable from position or from the
    /// parser's types when the table sits under a plain key. So the reader recovers the model type
    /// with a predicate, and the predicate has to be exact, because the C# and Python readers must
    /// agree on every document or <c>scene-check</c> fails on bytes.
    /// </para>
    /// <para>
    /// Exact means: <b>empty, or exactly the two string keys <c>guid</c> and <c>path</c></b>.
    /// Nothing about "looks scalar-ish", nothing about position. A payload table wanting those two
    /// string keys and nothing else IS a reference by this format's definition — that shape is
    /// reserved, and <c>verify</c> says so when a payload tries to use it for something else.
    /// </para>
    /// <para>
    /// The empty case is what makes the null slot work, and it is why an empty table is written
    /// <c>{}</c> rather than under a header: in these documents the only empty table that occurs
    /// is a reference to nothing.
    /// </para>
    /// </remarks>
    public static bool IsReferenceShaped(IReadOnlyCollection<KeyValuePair<string, object>> table)
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
