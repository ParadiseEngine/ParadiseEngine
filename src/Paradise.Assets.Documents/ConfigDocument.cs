namespace Paradise.Assets.Documents;

/// <summary>
/// Schema-free handling of authored config documents — the game's own TOML files, whose keys
/// belong to the game's schema, not to the engine.
/// </summary>
/// <remarks>
/// The engine can still do two things without the schema: check that a document parses, and
/// rewrite it canonically (the <c>config-check</c> half of the drift guard, and the
/// <c>document_format = "toml"</c> build output). Both go through the untyped model, so values
/// and structure survive exactly; comments do not — a canonical write is a machine write, and
/// the committed source keeps its comments because build output is a separate tree.
/// </remarks>
public static class ConfigDocument
{
    /// <summary>
    /// Parses <paramref name="toml"/> and re-renders it canonically.
    /// </summary>
    /// <param name="toml">The document text.</param>
    /// <param name="canonical">The canonical rendering, when parsing succeeded.</param>
    /// <param name="error">What was wrong with the document otherwise.</param>
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
    /// Renders an authored config document as JSON, for a <c>document_format = "json"</c> build.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Schema-free like the rest of this type: the untyped model goes straight across, so a key
    /// the engine has never heard of survives with its structure and its value intact. Only the
    /// SYNTAX changes.
    /// </para>
    /// <para>
    /// An inline table is written as an ordinary JSON object. The distinction between an inline
    /// table and a header table is a TOML spelling — it exists so a canonical write is
    /// byte-stable — and JSON has one way to write an object, so it does not survive and does not
    /// need to.
    /// </para>
    /// </remarks>
    /// <param name="toml">Canonical document text, as produced by <see cref="TryCanonicalize"/>.</param>
    /// <param name="sourceName">Named in the exception if the text will not parse.</param>
    /// <exception cref="FormatException">The text is not a readable config document.</exception>
    public static string ToJson(string toml, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(toml);

        var table = TomlDocumentReader.Parse(toml, problem => new FormatException($"{sourceName}: {problem}"));
        var model = TomlDocumentReader.ToCanonical(table, "in the document", problem => new FormatException($"{sourceName}: {problem}"));

        // JsonNode.ToJsonString rather than JsonSerializer.Serialize: it needs no reflection, so it
        // is trim- and AOT-safe, which the pipeline is built to stay.
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
