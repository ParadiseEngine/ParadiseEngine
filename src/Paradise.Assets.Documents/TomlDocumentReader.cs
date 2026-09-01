using Tomlyn;
using Tomlyn.Model;

namespace Paradise.Assets.Documents;

/// <summary>
/// The shared plumbing of the strict document readers: parse untyped TOML, then take typed
/// values out of it with errors that name the source, the key and what was expected.
/// </summary>
/// <remarks>
/// Untyped (<see cref="TomlTable"/>) on purpose — documents carry open payloads no static model
/// can enumerate — and strict about unknown keys: a typo a lenient reader ignored would be
/// silently dropped by the next machine rewrite, which is data loss with no error anywhere.
/// </remarks>
internal static class TomlDocumentReader
{
    /// <summary>Parses TOML text, converting Tomlyn's failure into <paramref name="fail"/>'s exception.</summary>
    public static TomlTable Parse(string toml, Func<string, Exception> fail)
    {
        TomlTable? table;
        try
        {
            table = TomlSerializer.Deserialize<TomlTable>(toml, UntypedTomlSerializerContext.Default);
        }
        catch (TomlException error)
        {
            throw fail($"is not valid TOML ({error.Message})");
        }

        return table ?? new TomlTable();
    }

    /// <summary>Throws via <paramref name="fail"/> when <paramref name="table"/> holds keys outside <paramref name="known"/>.</summary>
    public static void RejectUnknownKeys(TomlTable table, string context, IReadOnlyList<string> known, Func<string, Exception> fail)
    {
        foreach (var key in table.Keys)
        {
            if (!known.Contains(key, StringComparer.Ordinal))
            {
                throw fail($"has an unknown key '{key}' {context}; expected one of: {string.Join(", ", known)}");
            }
        }
    }

    public static long RequireInteger(TomlTable table, string key, string context, Func<string, Exception> fail)
    {
        if (!table.TryGetValue(key, out var value)) throw fail($"is missing '{key}' {context}");
        return value as long? ?? throw fail($"holds a {DescribeType(value)} where '{key}' {context} must be an integer");
    }

    public static string RequireString(TomlTable table, string key, string context, Func<string, Exception> fail)
    {
        if (!table.TryGetValue(key, out var value)) throw fail($"is missing '{key}' {context}");
        return value as string ?? throw fail($"holds a {DescribeType(value)} where '{key}' {context} must be a string");
    }

    public static string? OptionalString(TomlTable table, string key, string context, Func<string, Exception> fail)
    {
        if (!table.TryGetValue(key, out var value)) return null;
        return value as string ?? throw fail($"holds a {DescribeType(value)} where '{key}' {context} must be a string");
    }

    public static TomlTable? OptionalTable(TomlTable table, string key, string context, Func<string, Exception> fail)
    {
        if (!table.TryGetValue(key, out var value)) return null;
        return value as TomlTable ?? throw fail($"holds a {DescribeType(value)} where '{key}' {context} must be a table");
    }

    public static TomlTableArray? OptionalTableArray(TomlTable table, string key, string context, Func<string, Exception> fail)
    {
        if (!table.TryGetValue(key, out var value)) return null;
        return value as TomlTableArray ?? throw fail($"holds a {DescribeType(value)} where '{key}' {context} must be an array of tables");
    }

    /// <summary>Reads a fixed-length numeric array (integers accepted and widened).</summary>
    public static float[] RequireFloatArray(TomlTable table, string key, int length, string context, Func<string, Exception> fail)
    {
        if (!table.TryGetValue(key, out var value)) throw fail($"is missing '{key}' {context}");
        if (value is not TomlArray array || array.Count != length)
        {
            throw fail($"needs '{key}' {context} to be an array of {length} numbers");
        }

        var result = new float[length];
        for (var i = 0; i < length; i++)
        {
            result[i] = array[i] switch
            {
                double d => (float)d,
                long l => l,
                _ => throw fail($"needs '{key}' {context} to be an array of {length} numbers"),
            };
        }

        return result;
    }

    /// <summary>
    /// Converts a parsed payload table into the canonical model, preserving document order —
    /// which Tomlyn's model keeps, and which the export contract makes load-bearing.
    /// </summary>
    public static CanonicalTomlTable ToCanonical(TomlTable table, string context, Func<string, Exception> fail)
    {
        var result = new CanonicalTomlTable();
        foreach (var (key, value) in table)
        {
            result.Add(key, ToCanonicalValue(value, $"'{key}' {context}", fail));
        }

        return result;
    }

    /// <summary>
    /// A parsed table as the model type it was WRITTEN as: inline when it is asset-reference
    /// shaped, a generic table otherwise.
    /// </summary>
    /// <remarks>
    /// Deliberately decided from the CONTENT, not the parse. Tomlyn does remember the source form
    /// (<c>TomlTable.Kind</c>), but the Python mirror's <c>tomllib</c> does not, and both readers
    /// must rebuild the same model from the same bytes — so the shared rule is the shape. See
    /// <see cref="AssetReferenceCodec.IsWrittenInline"/> for why the predicate is exact rather
    /// than a judgement about how table-ish the contents look, and issue #187 for the decision
    /// record.
    /// </remarks>
    private static object ToCanonicalTable(TomlTable table, string context, Func<string, Exception> fail)
    {
        var pairs = new List<KeyValuePair<string, object>>();
        foreach (var (key, member) in table)
        {
            if (member is TomlTable or TomlTableArray)
            {
                // A nested table means this is structural, never a reference -- and an inline
                // table may not nest one, so the generic form is the only possibility.
                return ToCanonical(table, context, fail);
            }

            pairs.Add(new KeyValuePair<string, object>(key, ToCanonicalValue(member, $"'{key}' {context}", fail)));
        }

        if (!AssetReferenceCodec.IsWrittenInline(pairs)) return ToCanonical(table, context, fail);

        var inline = new CanonicalInlineTable();
        foreach (var (key, value) in pairs) inline.Add(key, value);
        return inline;
    }

    /// <summary>One document value as its model form. Public because the structural readers
    /// (scene, prefab) need it for the payload fields they carry through opaquely.</summary>
    public static object ToCanonicalValue(object? value, string context, Func<string, Exception> fail) => value switch
    {
        bool or long or double or string => value,
        TomlTable table => ToCanonicalTable(table, context, fail),
        TomlTableArray tables => tables.Select(element => ToCanonical(element, context, fail)).ToArray(),
        TomlArray array => array.Select(element => ToCanonicalElement(element, context, fail)).ToList(),
        null => throw fail($"holds an empty value at {context}"),
        _ => throw fail($"holds a {DescribeType(value)} at {context}, which authored documents do not use"),
    };

    /// <summary>
    /// One element of an array. A table here is always a <see cref="CanonicalInlineTable"/> —
    /// inline is the only form TOML allows inside an array (an array-of-tables arrives as a
    /// <see cref="TomlTableArray"/>, a different type entirely).
    /// </summary>
    private static object ToCanonicalElement(object? value, string context, Func<string, Exception> fail)
    {
        if (value is not TomlTable table) return ToCanonicalValue(value, context, fail);

        var inline = new CanonicalInlineTable();
        foreach (var (key, member) in table)
        {
            if (member is TomlTable or TomlTableArray)
            {
                throw fail($"nests a table inside an inline table at '{key}' {context}");
            }

            inline.Add(key, ToCanonicalValue(member, $"'{key}' {context}", fail));
        }

        return inline;
    }

    private static string DescribeType(object? value) => value switch
    {
        null => "nothing",
        bool => "boolean",
        long => "integer",
        double => "float",
        string => "string",
        TomlTable => "table",
        TomlArray => "array",
        TomlTableArray => "array of tables",
        _ => value.GetType().Name,
    };
}

/// <summary>
/// The source-generated context for the untyped model, which is how parsing to
/// <see cref="TomlTable"/> stays off Tomlyn's reflection path — the same discipline as every
/// typed context in these packages, applied to the one deliberately untyped read.
/// </summary>
[Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
internal sealed partial class UntypedTomlSerializerContext : Tomlyn.Serialization.TomlSerializerContext;
