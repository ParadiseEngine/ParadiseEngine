using Paradise.Authoring;

namespace Paradise.Assets.Documents;

/// <summary>
/// The wire form of an <see cref="AssetReference"/>: <c>{ guid = "…", path = "…" }</c> in that
/// order, the Python mirror producing the same bytes. An absent reference is <c>{}</c>, not an
/// omitted element, because references sit in arrays where position is meaning.
/// </summary>
public static class AssetReferenceCodec
{
    public const string GuidKey = "guid";

    public const string PathKey = "path";

    /// <summary>
    /// Whether a parsed table is a reference, decided from CONTENT because the Python mirror's
    /// <c>tomllib</c> cannot see the source form (issue #187): empty, or exactly the two string
    /// keys <c>guid</c> and <c>path</c>. That shape is therefore reserved.
    /// </summary>
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

    /// <exception cref="ArgumentException">The reference has one half only; the reader refuses that, and a path without a guid is exactly what the guid exists to prevent.</exception>
    public static CanonicalInlineTable Write(AssetReference? reference)
    {
        var table = new CanonicalInlineTable();
        if (reference is null || reference.IsEmpty) return table;
        if (reference.Guid == Guid.Empty || reference.Path.Length == 0)
        {
            throw new ArgumentException(
                $"An asset reference needs both a guid and a path; got guid '{DocumentGuid.Format(reference.Guid)}' and path '{reference.Path}'.",
                nameof(reference));
        }

        table.Add(GuidKey, DocumentGuid.Format(reference.Guid));
        table.Add(PathKey, reference.Path);
        return table;
    }

    public static AssetReference? Read(object? value, string context, Func<string, Exception> fail)
    {
        if (value is not CanonicalInlineTable table)
        {
            throw fail($"holds {Describe(value)} where an asset reference {{ guid, path }} was expected {context}");
        }

        if (table.Count == 0) return null;

        var guidText = table.Value(GuidKey) as string;
        var path = table.Value(PathKey) as string;

        // A path-only reference would resolve today and break on the first rename.
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
