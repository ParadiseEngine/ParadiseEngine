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

    /// <summary>Renders a config as JSON through <see cref="CanonicalJson"/> (integral floats become integers; <c>inf</c>/<c>nan</c> are refused).</summary>
    /// <exception cref="FormatException">The text is not a readable config document, or holds a float JSON cannot carry.</exception>
    public static string ToJson(string toml, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(toml);

        var table = TomlDocumentReader.Parse(toml, problem => new FormatException($"{sourceName}: {problem}"));
        var model = TomlDocumentReader.ToCanonical(table, "in the document", problem => new FormatException($"{sourceName}: {problem}"));

        try
        {
            // JsonNode.ToJsonString, not JsonSerializer.Serialize: no reflection, so AOT-safe.
            return CanonicalJson.ToNode(model).ToJsonString(s_jsonOptions);
        }
        catch (FormatException failure)
        {
            throw new FormatException($"{sourceName}: {failure.Message}", failure);
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
}
