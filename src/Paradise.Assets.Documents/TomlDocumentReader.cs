using Tomlyn;
using Tomlyn.Model;

namespace Paradise.Assets.Documents;

/// <summary>Shared plumbing of the strict readers. Untyped because payloads are open; strict about unknown keys because a typo a lenient reader ignored is dropped by the next machine rewrite, silently.</summary>
internal static class TomlDocumentReader
{
    private static readonly TomlSerializerOptions s_validation = new() { DuplicateKeyHandling = TomlDuplicateKeyHandling.Error };

    public static TomlTable Parse(string toml, Func<string, Exception> fail)
    {
        // Validated on the SYNTAX tree first: binding into TomlTable ignores DuplicateKeyHandling
        // and keeps the last value, which silently dropped the first (issue #198). The parser's
        // semantic pass refuses a redefined key at every level, as TOML and the Python mirror's
        // tomllib do.
        var syntax = Tomlyn.Parsing.SyntaxParser.Parse(toml, s_validation, sourceName: null, validate: true);
        if (syntax.HasErrors)
        {
            throw fail($"is not valid TOML ({string.Join("; ", syntax.Diagnostics.Select(static d => d.Message))})");
        }

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

    /// <summary>Document order is preserved (Tomlyn keeps it) because the contract makes it load-bearing.</summary>
    public static CanonicalTomlTable ToCanonical(TomlTable table, string context, Func<string, Exception> fail)
    {
        var result = new CanonicalTomlTable();
        foreach (var (key, value) in table)
        {
            result.Add(key, ToCanonicalValue(value, $"'{key}' {context}", fail));
        }

        return result;
    }

    private static object ToCanonicalTable(TomlTable table, string context, Func<string, Exception> fail)
    {
        var pairs = new List<KeyValuePair<string, object>>();
        foreach (var (key, member) in table)
        {
            if (member is TomlTable or TomlTableArray)
            {
                return ToCanonical(table, context, fail);
            }

            pairs.Add(new KeyValuePair<string, object>(key, ToCanonicalValue(member, $"'{key}' {context}", fail)));
        }

        if (!AssetReferenceCodec.IsWrittenInline(pairs)) return ToCanonical(table, context, fail);

        var inline = new CanonicalInlineTable();
        foreach (var (key, value) in pairs) inline.Add(key, value);
        return inline;
    }

    public static object ToCanonicalValue(object? value, string context, Func<string, Exception> fail) => value switch
    {
        bool or long or double or string => value,
        TomlTable table => ToCanonicalTable(table, context, fail),
        TomlTableArray tables => tables.Select(element => ToCanonical(element, context, fail)).ToArray(),
        TomlArray array => array.Select(element => ToCanonicalElement(element, context, fail)).ToList(),
        null => throw fail($"holds an empty value at {context}"),
        _ => throw fail($"holds a {DescribeType(value)} at {context}, which authored documents do not use"),
    };

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

/// <summary>Keeps even the untyped read off Tomlyn's reflection path (AOT). Duplicate keys are refused by <see cref="TomlDocumentReader.Parse"/>'s syntax validation, not here: the TomlTable binding does not consult the option.</summary>
[Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
internal sealed partial class UntypedTomlSerializerContext : Tomlyn.Serialization.TomlSerializerContext;
