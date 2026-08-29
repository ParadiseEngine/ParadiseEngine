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
}
