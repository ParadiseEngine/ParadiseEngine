using System.Text.Json.Nodes;

namespace Paradise.Assets.Documents;

/// <summary>Converts the canonical model to JSON for config compilation and prefab baking.</summary>
/// <remarks>Integral floats become JSON integers; non-finite numbers are rejected.</remarks>
public static class CanonicalJson
{
    /// <param name="table">The model, a <see cref="CanonicalTomlTable"/> or <see cref="CanonicalInlineTable"/>.</param>
    /// <param name="reference">
    /// How an asset reference (an inline table shaped <c>{ guid, path }</c> or empty) is rendered.
    /// Null keeps it as an object; the bake substitutes the built path.
    /// </param>
    /// <exception cref="FormatException">A float is <c>inf</c> or <c>nan</c>.</exception>
    public static JsonObject ToNode(
        IEnumerable<KeyValuePair<string, object>> table,
        Func<CanonicalInlineTable, JsonNode?>? reference = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        return ToObject(table, "", reference);
    }

    private static JsonObject ToObject(
        IEnumerable<KeyValuePair<string, object>> table,
        string path,
        Func<CanonicalInlineTable, JsonNode?>? reference)
    {
        var result = new JsonObject();
        foreach (var (key, value) in table)
        {
            result[key] = ToValue(value, path.Length == 0 ? key : $"{path}.{key}", reference);
        }

        return result;
    }

    private static JsonNode? ToValue(object? value, string path, Func<CanonicalInlineTable, JsonNode?>? reference) => value switch
    {
        null => null,
        CanonicalInlineTable inline when reference is not null && AssetReferenceCodec.IsWrittenInline(inline) => reference(inline),
        CanonicalInlineTable inline => ToObject(inline, path, reference),
        CanonicalTomlTable nested => ToObject(nested, path, reference),
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        long integer => JsonValue.Create(integer),
        double number when double.IsFinite(number) => JsonValue.Create(number),
        double number => throw new FormatException(
            $"holds {CanonicalTomlWriter.FormatFloat(number)} at '{path}', which JSON cannot carry"),
        IReadOnlyList<object> list => new JsonArray(list.Select((item, i) => ToValue(item, $"{path}[{i}]", reference)).ToArray()),
        _ => throw new FormatException($"holds a {value.GetType().Name} at '{path}', which is not a document value"),
    };
}
