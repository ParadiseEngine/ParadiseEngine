namespace Paradise.Assets.Documents;

/// <summary>Schema-free handling of the game's own TOML: parse-check and canonical rewrite. Comments do not survive; the committed source keeps them because build output is a separate tree.</summary>
public static class ConfigDocument
{
    public static bool TryCanonicalize(string toml, out string canonical, out string error)
    {
        ArgumentNullException.ThrowIfNull(toml);

        canonical = "";
        error = "";
        try
        {
            var table = TomlDocumentReader.Parse(toml, static problem => new FormatException(problem));
            var model = TomlDocumentReader.ToCanonical(table, "in the document", static problem => new FormatException(problem));
            canonical = CanonicalTomlWriter.WriteString(model);
            return true;
        }
        catch (FormatException failure)
        {
            error = failure.Message;
            return false;
        }
    }

    /// <summary>
    /// Renders a config as JSON. Not byte-exact: an integral float becomes an integer
    /// (<c>1.0</c> → <c>1</c>), and <c>inf</c>/<c>nan</c> currently throw
    /// <see cref="ArgumentException"/> rather than the documented one (issue #211).
    /// </summary>
    /// <exception cref="FormatException">The text is not a readable config document.</exception>
    public static string ToJson(string toml, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(toml);

        var table = TomlDocumentReader.Parse(toml, problem => new FormatException($"{sourceName}: {problem}"));
        var model = TomlDocumentReader.ToCanonical(table, "in the document", problem => new FormatException($"{sourceName}: {problem}"));

        // JsonNode.ToJsonString, not JsonSerializer.Serialize: no reflection, so AOT-safe.
        return ToNode(model)!.ToJsonString(s_jsonOptions);

        static System.Text.Json.Nodes.JsonNode? ToNode(IEnumerable<KeyValuePair<string, object>> pairs)
        {
            var result = new System.Text.Json.Nodes.JsonObject();
            foreach (var (key, value) in pairs) result[key] = ToValue(value);
            return result;
        }

        static System.Text.Json.Nodes.JsonNode? ToValue(object? value) => value switch
        {
            null => null,
            CanonicalInlineTable inline => ToNode(inline),
            CanonicalTomlTable nested => ToNode(nested),
            string text => System.Text.Json.Nodes.JsonValue.Create(text),
            bool flag => System.Text.Json.Nodes.JsonValue.Create(flag),
            long integer => System.Text.Json.Nodes.JsonValue.Create(integer),
            double number => System.Text.Json.Nodes.JsonValue.Create(number),
            IReadOnlyList<object> list => new System.Text.Json.Nodes.JsonArray(list.Select(ToValue).ToArray()),
            _ => System.Text.Json.Nodes.JsonValue.Create(value.ToString()),
        };
    }

    private static readonly System.Text.Json.JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
}
